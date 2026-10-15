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

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetWindowDC(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("gdi32.dll", SetLastError = true)] static extern int SaveDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool RestoreDC(IntPtr dc, int saved);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool SetViewportOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    /// <summary>
    /// Paints one window into a DC whose viewport is shifted to the window's place on
    /// the desktop, so a single desktop-sized DIB section covers every window.
    /// PrintWindow is preferred (it asks the window to render itself and needs no screen
    /// DC - the container's service session has none); BitBlt from the window DC is the
    /// fallback.
    /// </summary>
    public static bool Paint(IntPtr dc, long id, int x, int y, int w, int h) {
        var hwnd = new IntPtr(id);
        if (!IsWindowVisible(hwnd)) return false;
        int saved = SaveDC(dc);
        try {
            SetViewportOrgEx(dc, x, y, IntPtr.Zero);
            if (PrintWindow(hwnd, dc, 0x2)) return true;
            var src = GetWindowDC(hwnd);
            if (src == IntPtr.Zero) return false;
            try { return BitBlt(dc, 0, 0, w, h, src, 0, 0, 0x00CC0020); }
            finally { ReleaseDC(hwnd, src); }
        } finally { RestoreDC(dc, saved); }
    }

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

# ---- the pixel buffer is a GDI DIB section mapped onto the shared file -------------
# GDI paints straight into shared memory: no CopyFromScreen, no memcpy - and unlike a
# screen grab it works in a session with no display device (a container's service
# session), which is exactly where HCS-created processes live.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WsmMirrorDib {
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER {
        public int biSize; public int biWidth; public int biHeight; public short biPlanes;
        public short biBitCount; public int biCompression; public int biSizeImage;
        public int biXPelsPerMeter; public int biYPelsPerMeter; public int biClrUsed; public int biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct RGBQUAD { public byte b, g, r, a; }
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO { public BITMAPINFOHEADER header; public RGBQUAD colors; }

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] public static extern bool DeleteObject(IntPtr obj);

    /// <summary>Creates a top-down 32bpp DIB whose pixels are <paramref name="section"/> at <paramref name="offset"/>.</summary>
    public static IntPtr Create(int width, int height, IntPtr section, uint offset, out int error) {
        var info = new BITMAPINFO();
        info.header.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        info.header.biWidth = width;
        info.header.biHeight = -height;   // top-down: the shared buffer is top-down BGRX
        info.header.biPlanes = 1;
        info.header.biBitCount = 32;
        info.header.biCompression = 0;    // BI_RGB
        IntPtr bits;
        var dib = CreateDIBSection(IntPtr.Zero, ref info, 0, out bits, section, offset);
        error = Marshal.GetLastWin32Error();
        return dib;
    }
}
'@

$pixelSection = [IntPtr]::Add($base, 64)
$dibError = 0
$dib = [WsmMirrorDib]::Create($Width, $Height, $mapping, 64, [ref]$dibError)
if ($dib -eq [IntPtr]::Zero) {
    Log ("CreateDIBSection over the shared section failed: " + $dibError)
    throw ("CreateDIBSection failed: " + $dibError)
}
$memoryDc = [WsmMirrorDib]::CreateCompatibleDC([IntPtr]::Zero)
[void][WsmMirrorDib]::SelectObject($memoryDc, $dib)
Log ("DIB section over shared memory ready (dc=$memoryDc dib=$dib)")

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

        $windows = [WsmMirrorWindows]::List($TitleFilter)
        $sequence++
        $count = [Math]::Min($windows.Count, [int](8192 / 40))
        $marshal::WriteInt32($base, 40, $count)

        for ($i = 0; $i -lt $count; $i++) {
            $w = $windows[$i]
            $x = [Math]::Max(0, $w.X); $y = [Math]::Max(0, $w.Y)
            $right = [Math]::Min($Width, $w.X + $w.W); $bottom = [Math]::Min($Height, $w.Y + $w.H)
            $cw = $right - $x; $ch = $bottom - $y

            # GDI paints the window straight into the shared DIB section: no copy at all,
            # and no screen DC (which the container's service session does not have).
            $painted = $false
            if ($cw -gt 0 -and $ch -gt 0) {
                $painted = [WsmMirrorWindows]::Paint($memoryDc, $w.Id, $x, $y, $cw, $ch)
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
            if (-not $painted) { Log ("paint failed for 0x{0:X}" -f [int64]$w.Id) }
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
try { [void][WsmMirrorDib]::DeleteObject($dib); [void][WsmMirrorDib]::DeleteDC($memoryDc) } catch { }
[void][WsmMirrorNative]::UnmapViewOfFile($base)
[void][WsmMirrorNative]::CloseHandle($mapping)
[void][WsmMirrorNative]::CloseHandle($file)
