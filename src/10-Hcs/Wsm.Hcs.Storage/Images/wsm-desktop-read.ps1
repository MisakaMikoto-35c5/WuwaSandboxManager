# wsm-desktop-read.ps1 - the window-mirror READ SIDE.
#
# Installed into the container's image by
# Wsm.Hcs.Storage:IContainerImageCustomizer.EnableWindowMirror and started at logon
# from C:\wsm\wsm-winlogon-wrap.cmd, i.e. INSIDE the container's own interactive
# session (that is the only place that can see the container's windows - measured:
# docs/investigation/13-container-desktop-and-rdp.md sections 16-18).
#
# It publishes one shared file that contains
#   1. the desktop frame (top-down BGRX), and
#   2. a table of top-level windows (rect, title, sequence),
# and it only rewrites the regions of windows whose pixels actually changed, so the
# transport carries dirty regions instead of whole frames.
#
# Shared file layout (little endian, must match WindowMirrorDesktopProvider):
#   0  magic 'WSMM' (0x4D4D5357)  4  version (1)
#   8  width                     12 height
#   16 stride                    20 fps
#   24 seq (int64, ++ per pass)   32 busy (int32)
#   36 writerPid (int32)         40 windowCount (int32)
#   48 totalFrames (int64)
#   64 pixels (height * stride, top-down BGRX)
#   pixels + 0      window table, 8192 bytes, 40 bytes per entry:
#                   id(8) x(4) y(4) w(4) h(4) seq(8) titleOffset(4) titleLength(4)
#   pixels + 8192   title blob, 16384 bytes (UTF-16)
#
# Input arrives as one file per command in the agent directory (input-*.txt) and is
# injected in-process; agent-stop.txt makes it exit.
param(
    [string] $AgentDirectory = 'C:\wsm',
    [string] $MirrorFile = 'mirror.shm',
    [string] $InputPrefix = 'input-',
    [string] $StopFile = 'agent-stop.txt',
    [int] $Width = 1024,
    [int] $Height = 768,
    [int] $Fps = 4,
    [int] $Seconds = 86400,
    [string] $TitleFilter = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WsmMirrorNative {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr sa, uint protect, uint maxHi, uint maxLo, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHi, uint offLo, ulong length);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern void RtlMoveMemory(IntPtr dest, IntPtr src, UIntPtr length);
    [DllImport("msvcrt.dll", EntryPoint = "memcmp", CallingConvention = CallingConvention.Cdecl)]
    public static extern int memcmp(IntPtr a, IntPtr b, UIntPtr count);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern short VkKeyScanW(char c);
}
'@

$log = Join-Path $AgentDirectory 'read.log'
function Log([string] $message) {
    try { Add-Content -Path $log -Value (('{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $message)) -ErrorAction SilentlyContinue } catch { }
}

# ---- input injection (in process: a child per event would be far too slow) ----
$virtualKeys = @{
    'ENTER' = 0x0D; 'ESC' = 0x1B; 'TAB' = 0x09; 'SPACE' = 0x20; 'BACKSPACE' = 0x08; 'DELETE' = 0x2E
    'UP' = 0x26; 'DOWN' = 0x28; 'LEFT' = 0x25; 'RIGHT' = 0x27; 'HOME' = 0x24; 'END' = 0x23
    'PGUP' = 0x21; 'PGDN' = 0x22; 'INSERT' = 0x2D; 'CTRL' = 0x11; 'ALT' = 0x12; 'SHIFT' = 0x10
}
for ($i = 1; $i -le 24; $i++) { $virtualKeys["F$i"] = 0x6F + $i }

function Send-Move([int] $x, [int] $y) {
    [void][WsmMirrorNative]::SetCursorPos($x, $y)
    [WsmMirrorNative]::mouse_event(0x0001, 0, 0, 0, [IntPtr]::Zero)
}
function Send-Click([int] $x, [int] $y, [int] $button) {
    Send-Move $x $y
    Start-Sleep -Milliseconds 40
    if ($button -eq 2) {
        [WsmMirrorNative]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30
        [WsmMirrorNative]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero)
    } elseif ($button -eq 3) {
        [WsmMirrorNative]::mouse_event(0x0020, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30
        [WsmMirrorNative]::mouse_event(0x0040, 0, 0, 0, [IntPtr]::Zero)
    } else {
        [WsmMirrorNative]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30
        [WsmMirrorNative]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    }
}
function Send-Keys([string] $spec) {
    $keys = @()
    foreach ($part in ($spec -split '\+')) {
        $token = $part.Trim().ToUpperInvariant()
        if ($token.StartsWith('VK_')) { $token = $token.Substring(3) }
        if ($virtualKeys.ContainsKey($token)) { $keys += [byte]$virtualKeys[$token] }
        elseif ($token.Length -eq 1 -and $token -match '^[A-Z0-9]$') { $keys += [byte][char]$token }
        elseif ($token -match '^0X[0-9A-F]+$') { $keys += [byte][Convert]::ToInt32($token.Substring(2), 16) }
    }
    if ($keys.Count -eq 0) { return }
    foreach ($key in $keys) { [WsmMirrorNative]::keybd_event($key, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 15 }
    Start-Sleep -Milliseconds 40
    for ($i = $keys.Count - 1; $i -ge 0; $i--) { [WsmMirrorNative]::keybd_event($keys[$i], 0, 2, [IntPtr]::Zero); Start-Sleep -Milliseconds 15 }
}
function Send-Text([string] $text) {
    foreach ($character in $text.ToCharArray()) {
        $scan = [WsmMirrorNative]::VkKeyScanW($character)
        if ($scan -eq -1) { continue }
        $key = [byte]($scan -band 0xff)
        $shift = ($scan -band 0x100) -ne 0
        $ctrl = ($scan -band 0x200) -ne 0
        if ($shift) { [WsmMirrorNative]::keybd_event(0x10, 0, 0, [IntPtr]::Zero) }
        if ($ctrl) { [WsmMirrorNative]::keybd_event(0x11, 0, 0, [IntPtr]::Zero) }
        [WsmMirrorNative]::keybd_event($key, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 15
        [WsmMirrorNative]::keybd_event($key, 0, 2, [IntPtr]::Zero); Start-Sleep -Milliseconds 15
        if ($ctrl) { [WsmMirrorNative]::keybd_event(0x11, 0, 2, [IntPtr]::Zero) }
        if ($shift) { [WsmMirrorNative]::keybd_event(0x10, 0, 2, [IntPtr]::Zero) }
    }
}
function Invoke-InputLine([string] $line) {
    if ($line -like 'move *') {
        $xy = ($line -replace '^move\s+', '') -split ','
        Send-Move ([int]$xy[0]) ([int]$xy[1])
    } elseif ($line -like 'click *') {
        $xy = ($line -replace '^click\s+', '') -split ','
        $button = 1
        if ($xy.Count -gt 2) { $button = [int]$xy[2] }
        Send-Click ([int]$xy[0]) ([int]$xy[1]) $button
    } elseif ($line -like 'keys *') {
        Send-Keys ($line -replace '^keys\s+', '')
    } elseif ($line -like 'text *') {
        Send-Text $line.Substring('text '.Length)
    } elseif ($line -like 'movewindow *') {
        # The mirror window was dragged on the host side: move the real window.
        $rest = ($line -replace '^movewindow\s+', '') -split '\s+'
        $xy = $rest[1] -split ','
        $moved = [WsmMirrorWindows]::Move([int64]$rest[0], [int]$xy[0], [int]$xy[1])
        Log ("movewindow 0x{0:X} -> ({1},{2}) ok={3}" -f [int64]$rest[0], [int]$xy[0], [int]$xy[1], $moved)
    } elseif ($line -like 'activate *') {
        [void][WsmMirrorWindows]::Activate([int64]($line -replace '^activate\s+', ''))
    }
}

# ---- top-level windows of the desktop we are on ----
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WsmMirrorWindows {
    public class Entry { public long Id; public int X, Y, W, H; public string Title; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder t, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder t, int max);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr GetPropW(IntPtr h, string name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    public static List<Entry> List(string titleFilter) {
        var list = new List<Entry>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            if (IsIconic(h)) return true;
            if ((GetWindowLong(h, -20) & 0x00000080) != 0) return true; // WS_EX_TOOLWINDOW
            if (GetPropW(h, "WSM_MIRROR_HOST") != IntPtr.Zero) return true; // never mirror a mirror
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            if (sb.Length == 0) return true;
            if (titleFilter != null && titleFilter.Length > 0 &&
                sb.ToString().IndexOf(titleFilter, StringComparison.OrdinalIgnoreCase) < 0) return true;
            RECT r; GetWindowRect(h, out r);
            int w = r.R - r.L, hh = r.B - r.T;
            if (w < 24 || hh < 24) return true;
            var e = new Entry();
            e.Id = h.ToInt64(); e.X = r.L; e.Y = r.T; e.W = w; e.H = hh; e.Title = sb.ToString();
            list.Add(e);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Moves a window without resizing, restacking or activating it.</summary>
    public static bool Move(long id, int x, int y) {
        const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        return SetWindowPos(new IntPtr(id), IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Brings a window to the foreground (a click on a mirrored window).</summary>
    public static bool Activate(long id) {
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
        return SetWindowPos(new IntPtr(id), new IntPtr(-1), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE);
    }
}
'@

# ---- shared mirror file ----
$stride = $Width * 4
$pixels = $stride * $Height
$tableOffset = 64 + $pixels
$titleOffset = $tableOffset + 8192
$capacity = $titleOffset + 16384
$mirrorPath = Join-Path $AgentDirectory $MirrorFile
$stopPath = Join-Path $AgentDirectory $StopFile

if (-not (Test-Path $AgentDirectory)) { New-Item -ItemType Directory -Path $AgentDirectory -Force | Out-Null }
if (Test-Path $mirrorPath) { Remove-Item $mirrorPath -Force -ErrorAction SilentlyContinue }

$file = [WsmMirrorNative]::CreateFileW($mirrorPath, [uint32]3221225472, [uint32]7, [IntPtr]::Zero, [uint32]2, [uint32]0x80, [IntPtr]::Zero)
if ($file -eq [IntPtr](-1)) { throw ("CreateFileW failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
$mapping = [WsmMirrorNative]::CreateFileMappingW($file, [IntPtr]::Zero, [uint32]4, 0, [uint32]$capacity, $null)
if ($mapping -eq [IntPtr]::Zero) { throw ("CreateFileMappingW failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
$base = [WsmMirrorNative]::MapViewOfFile($mapping, [uint32]0xF001F, 0, 0, [uint64]$capacity)
if ($base -eq [IntPtr]::Zero) { throw ("MapViewOfFile failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }

$marshal = [Runtime.InteropServices.Marshal]
$marshal::WriteInt32($base, 0, [int]0x4D4D5357)
$marshal::WriteInt32($base, 4, 1)
$marshal::WriteInt32($base, 8, $Width)
$marshal::WriteInt32($base, 12, $Height)
$marshal::WriteInt32($base, 16, $stride)
$marshal::WriteInt32($base, 20, $Fps)
$marshal::WriteInt64($base, 24, [int64]0)
$marshal::WriteInt32($base, 32, 0)
$marshal::WriteInt32($base, 36, $PID)
$marshal::WriteInt32($base, 40, 0)
$marshal::WriteInt64($base, 48, [int64]0)

# Capture into a normal managed bitmap (a Bitmap built over the mapped pointer
# renders black here) and compare against the last published pixels to find the
# windows that actually changed.
$target = [IntPtr]::Add($base, 64)
$bounds = New-Object Drawing.Rectangle(0, 0, $Width, $Height)
$size = New-Object Drawing.Size($Width, $Height)
$bitmap = New-Object Drawing.Bitmap($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
$shadow = New-Object Drawing.Bitmap($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)

Log ("read side started pid=$PID ${Width}x${Height} stride=$stride fps=$Fps seconds=$Seconds mirror=$mirrorPath")

$sequence = [int64]0
$frames = [int64]0
$seen = @{}
$deadline = (Get-Date).AddSeconds($Seconds)
$delay = [Math]::Max(1, [int](1000 / [Math]::Max(1, $Fps)))

while ((Get-Date) -lt $deadline) {
    if (Test-Path $stopPath) { Log 'stop marker seen'; break }

    try {
        $marshal::WriteInt32($base, 32, 1)   # busy
        $graphics.CopyFromScreen(0, 0, 0, 0, $size)

        $windows = [WsmMirrorWindows]::List($TitleFilter)
        $sequence++
        $count = [Math]::Min($windows.Count, [int](8192 / 40))
        $marshal::WriteInt32($base, 40, $count)

        $currentData = $bitmap.LockBits($bounds, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
        $shadowData = $shadow.LockBits($bounds, [Drawing.Imaging.ImageLockMode]::WriteOnly, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
        try {
            for ($i = 0; $i -lt $count; $i++) {
                $w = $windows[$i]
                $x = [Math]::Max(0, $w.X); $y = [Math]::Max(0, $w.Y)
                $right = [Math]::Min($Width, $w.X + $w.W); $bottom = [Math]::Min($Height, $w.Y + $w.H)
                $cw = $right - $x; $ch = $bottom - $y
                $dirty = $false
                if ($cw -gt 0 -and $ch -gt 0) {
                    for ($row = 0; $row -lt $ch; $row++) {
                        $src = [IntPtr]::Add($currentData.Scan0, ($y + $row) * $currentData.Stride + $x * 4)
                        $old = [IntPtr]::Add($shadowData.Scan0, ($y + $row) * $shadowData.Stride + $x * 4)
                        if ([WsmMirrorNative]::memcmp($src, $old, [UIntPtr]([uint64]($cw * 4))) -ne 0) { $dirty = $true; break }
                    }
                }

                $entry = $tableOffset + $i * 40
                $marshal::WriteInt64($base, $entry, [int64]$w.Id)
                $marshal::WriteInt32($base, $entry + 8, $w.X)
                $marshal::WriteInt32($base, $entry + 12, $w.Y)
                $marshal::WriteInt32($base, $entry + 16, $w.W)
                $marshal::WriteInt32($base, $entry + 20, $w.H)
                $marshal::WriteInt64($base, $entry + 24, [int64]$sequence)
                $titleBytes = [Text.Encoding]::Unicode.GetBytes($w.Title)
                if ($titleBytes.Length -gt 512) { $titleBytes = $titleBytes[0..511] }
                $marshal::Copy($titleBytes, 0, [IntPtr]::Add($base, $titleOffset + $i * 512), $titleBytes.Length)
                $marshal::WriteInt32($base, $entry + 32, $i * 512)
                $marshal::WriteInt32($base, $entry + 36, [int]($titleBytes.Length / 2))

                # Only the windows that changed are written into the shared file.
                if ($dirty -and $cw -gt 0 -and $ch -gt 0) {
                    for ($row = 0; $row -lt $ch; $row++) {
                        $src = [IntPtr]::Add($currentData.Scan0, ($y + $row) * $currentData.Stride + $x * 4)
                        $dst = [IntPtr]::Add($target, (($y + $row) * $stride) + $x * 4)
                        $shadowRow = [IntPtr]::Add($shadowData.Scan0, ($y + $row) * $shadowData.Stride + $x * 4)
                        [WsmMirrorNative]::RtlMoveMemory($dst, $src, [UIntPtr]([uint64]($cw * 4)))
                        [WsmMirrorNative]::RtlMoveMemory($shadowRow, $src, [UIntPtr]([uint64]($cw * 4)))
                    }
                }
            }
        } finally {
            $bitmap.UnlockBits($currentData)
            $shadow.UnlockBits($shadowData)
        }

        $frames++
        $marshal::WriteInt64($base, 48, $frames)
        $marshal::WriteInt64($base, 24, $sequence)
        $marshal::WriteInt32($base, 32, 0)   # not busy
    } catch {
        $marshal::WriteInt32($base, 32, 0)
        Log ('capture failed: ' + $_.Exception.Message)
        Start-Sleep -Seconds 1
        continue
    }

    # ---- host input: every not-yet-seen queue file, in order ----
    try {
        $pending = @(Get-ChildItem -Path $AgentDirectory -Filter ($InputPrefix + '*.txt') -ErrorAction SilentlyContinue | Sort-Object Name)
        foreach ($fileInfo in $pending) {
            if ($seen.ContainsKey($fileInfo.Name)) { continue }
            $seen[$fileInfo.Name] = $true
            $command = ''
            $stream = $null
            try {
                $stream = [IO.File]::Open($fileInfo.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                $reader = New-Object IO.StreamReader($stream)
                $command = $reader.ReadToEnd()
                $reader.Dispose()
            } catch {
            } finally {
                if ($stream) { $stream.Dispose() }
            }
            foreach ($line in ($command -split "`r?`n")) {
                $trimmed = $line.Trim()
                if ($trimmed -eq '') { continue }
                try { Invoke-InputLine $trimmed } catch { Log ('input failed: ' + $_.Exception.Message) }
            }
        }
    } catch {
        Log ('input queue failed: ' + $_.Exception.Message)
    }

    Start-Sleep -Milliseconds $delay
}

$marshal::WriteInt32($base, 32, 0)
Log ("read side exiting after frames=$frames windowsPublishes=$sequence")
try { $graphics.Dispose(); $bitmap.Dispose(); $shadow.Dispose() } catch { }
[void][WsmMirrorNative]::UnmapViewOfFile($base)
[void][WsmMirrorNative]::CloseHandle($mapping)
[void][WsmMirrorNative]::CloseHandle($file)
