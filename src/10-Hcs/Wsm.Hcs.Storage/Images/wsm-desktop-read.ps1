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
# Sharing mode (shm-mode.txt in the agent directory, written by the host before the
# read side starts; absent = 'file'):
#   file     - mirror.shm carries header + pixels (the host maps the file).
#   pagefile - mirror.shm carries ONLY the header (4 KB) and the pixels live in a
#              page-file-backed section this process creates; the host finds that
#              section by duplicating the handle out of this process (needs an
#              elevated reader; named sections do not cross the silo). The mode
#              field at offset 60 of the header tells the host which one it is.
#
# Shared file layout (little endian, must match ContainerDesktopMirror):
#   0  magic 'WSMM' (0x4D4D5357)  4  version (2)
#   8  width                     12 height
#   16 stride                    20 fps
#   24 seq (int64, ++ per pass)   32 busy (int32)
#   36 writerPid (int32)         40 windowCount (int32)
#   48 totalFrames (int64)       56 dirtyCount (int32)
#   60 mode (int32: 0 = file, 1 = pagefile)
#   64 pixels (height * stride, top-down BGRX)
#   pixels + 0      window table, 8192 bytes, 40 bytes per entry:
#                   id(8) x(4) y(4) w(4) h(4) seq(8) titleOffset(4) titleLength(4)
#   pixels + 8192   dirty rects, 3264 bytes, 16 bytes per entry: x(4) y(4) w(4) h(4)
#   pixels + 11456  title blob, 16384 bytes (UTF-16)
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
    [string] $TitleFilter = '',
    [string] $StartProgram = '',
    [switch] $Detached,
    [switch] $AsShell
)

$ErrorActionPreference = 'Stop'

# Logging comes first: both the parent (which detaches) and the child (which does the work)
# write here, and the whole body runs inside a try so a failure in the C# blocks below can
# no longer kill the script before its first log line (which looked exactly like
# "the read side never started").
$log = Join-Path $AgentDirectory 'read.log'
function Log([string] $message) {
    try { Add-Content -Path $log -Value (('{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $message)) -ErrorAction SilentlyContinue } catch { }
}
Log ("invoked pid=$PID detached=$($Detached.IsPresent) asShell=$($AsShell.IsPresent) user=$([Environment]::UserName) dir=$AgentDirectory")

# Self-detach: HCS waits for the process it created to exit, so the read side must not be that
# process. It re-launches itself through Start-Process (which detaches) and exits - so the real
# read side is created exactly like any other process in the session (no cmd/`start`, which kept
# the launcher alive for the child's lifetime and parked HCS process creation).
# In shell mode (-AsShell, launched by wsm-shell.cmd as Winlogon\Shell) there is no HCS to wait
# on us; the wrapper is the session's lifetime, so we stay in its foreground instead.
if (-not $Detached -and -not $AsShell) {
    $parts = @(
        '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Detached'
        '-AgentDirectory "' + $AgentDirectory + '"'
        '-MirrorFile "' + $MirrorFile + '"'
        '-InputPrefix "' + $InputPrefix + '"'
        '-StopFile "' + $StopFile + '"'
        '-Width ' + $Width
        '-Height ' + $Height
        '-Fps ' + $Fps
        '-Seconds ' + $Seconds
    )
    if ($TitleFilter) { $parts += ('-TitleFilter "' + $TitleFilter + '"') }
    if ($StartProgram) { $parts += ('-StartProgram "' + $StartProgram + '"') }
    $commandLine = $parts -join ' '
    Log ('detaching: powershell.exe ' + $commandLine)
    try {
        Start-Process -FilePath 'powershell.exe' -ArgumentList $commandLine -WindowStyle Hidden | Out-Null
        Log 'parent exiting after detaching'
    } catch {
        Log ('detach failed: ' + $_.Exception.Message)
        throw
    }
    exit 0
}

# Shell mode must never let this script terminate: it IS the session's shell, and a shell
# that exits tears the console session down. A fatal error (mirror file locked, a DC that
# cannot be created) therefore restarts the whole body after a pause instead of throwing.
$relaunch = $false
do {
$relaunch = $false

try {

Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
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
    [DllImport("msvcrt.dll", EntryPoint = "memset", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr memset(IntPtr dest, int value, UIntPtr count);
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool GdiFlush();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetUserObjectInformationW(IntPtr obj, int index, System.Text.StringBuilder info, int length, out uint needed);

    /// <summary>
    /// Moves this thread to the session's ACTIVE desktop. Screen/window capture only
    /// produces pixels there; on any other desktop GDI reads come back empty
    /// (measured in the container's services session).
    /// </summary>
    public static string SwitchToInputDesktop() {
        IntPtr input = OpenInputDesktop(0, false, 0x02000000); // MAXIMUM_ALLOWED
        if (input == IntPtr.Zero) return "OpenInputDesktop failed: " + Marshal.GetLastWin32Error();
        var name = new System.Text.StringBuilder(256);
        uint needed;
        GetUserObjectInformationW(input, 2, name, 512, out needed);
        if (!SetThreadDesktop(input)) {
            int error = Marshal.GetLastWin32Error();
            CloseDesktop(input);
            return "SetThreadDesktop(" + name + ") failed: " + error;
        }
        return "ok, thread desktop is now '" + name + "'";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public HARDWAREINPUT hi; }
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public INPUTUNION u; }
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    public const uint KeyeventfExtendedKey = 0x0001;
    public const uint KeyeventfKeyUp = 0x0002;
    public const uint KeyeventfUnicode = 0x0004;

    /// <summary>One keyboard event through SendInput (vk or a Unicode code unit in scan).</summary>
    public static uint KeyEvent(ushort vk, ushort scan, uint flags) {
        var input = new INPUT();
        input.type = 1;   // INPUT_KEYBOARD
        input.u.ki.wVk = vk;
        input.u.ki.wScan = scan;
        input.u.ki.dwFlags = flags;
        var inputs = new INPUT[] { input };
        return SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    [DllImport("user32.dll")] public static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(int threadId);
    [DllImport("kernel32.dll")] public static extern int GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder info, int length, out int needed);

    /// <summary>The name of a window station or desktop handle (for the log).</summary>
    public static string Name(IntPtr handle) {
        if (handle == IntPtr.Zero) return "(null)";
        var sb = new StringBuilder(256);
        int needed;
        GetUserObjectInformationW(handle, 2, sb, sb.Capacity * 2, out needed);
        return sb.ToString();
    }
}

/// <summary>
/// Per-window dirty tracking: the shadow buffer holds the previously published pixels,
/// and each pass compares the window's rect in 8x8 blocks. Only changed blocks are
/// copied into the shadow and their union becomes the window's dirty rect.
/// </summary>
public static class WsmMirrorDirty {
    [DllImport("msvcrt.dll", EntryPoint = "memcmp", CallingConvention = CallingConvention.Cdecl)]
    static extern int memcmp(IntPtr a, IntPtr b, UIntPtr count);
    [DllImport("kernel32.dll")] static extern void RtlMoveMemory(IntPtr dest, IntPtr src, UIntPtr length);

    public const int Block = 8;

    /// <summary>
    /// Compares the rect (x,y,w,h) of two top-down 32bpp frames block by block, copies the
    /// changed blocks from current into shadow, and reports their union. False = unchanged.
    /// </summary>
    public static bool Diff(IntPtr current, IntPtr shadow, int stride, int x, int y, int w, int h,
        out int dx, out int dy, out int dw, out int dh) {
        dx = 0; dy = 0; dw = 0; dh = 0;
        long cur = current.ToInt64(), sha = shadow.ToInt64();
        int left = w, top = h, right = 0, bottom = 0;
        for (int by = 0; by < h; by += Block) {
            int bh = Math.Min(Block, h - by);
            for (int bx = 0; bx < w; bx += Block) {
                int bw = Math.Min(Block, w - bx);
                bool same = true;
                for (int r = 0; r < bh; r++) {
                    long off = (long)(y + by + r) * stride + (long)(x + bx) * 4;
                    if (memcmp(new IntPtr(cur + off), new IntPtr(sha + off), (UIntPtr)(uint)(bw * 4)) != 0) { same = false; break; }
                }
                if (same) continue;
                for (int r = 0; r < bh; r++) {
                    long off = (long)(y + by + r) * stride + (long)(x + bx) * 4;
                    RtlMoveMemory(new IntPtr(sha + off), new IntPtr(cur + off), (UIntPtr)(uint)(bw * 4));
                }
                if (bx < left) left = bx;
                if (by < top) top = by;
                if (bx + bw > right) right = bx + bw;
                if (by + bh > bottom) bottom = by + bh;
            }
        }
        if (right <= left || bottom <= top) return false;
        dx = x + left; dy = y + top; dw = right - left; dh = bottom - top;
        return true;
    }

    /// <summary>Copies a rect between two frames with the same stride (shadow init after a move/resize).</summary>
    public static void Copy(IntPtr dest, IntPtr src, int stride, int x, int y, int w, int h) {
        long d = dest.ToInt64(), s = src.ToInt64();
        for (int r = 0; r < h; r++) {
            long off = (long)(y + r) * stride + (long)x * 4;
            RtlMoveMemory(new IntPtr(d + off), new IntPtr(s + off), (UIntPtr)(uint)(w * 4));
        }
    }
}
'@

$log = Join-Path $AgentDirectory 'read.log'
function Log([string] $message) {
    try { Add-Content -Path $log -Value (('{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $message)) -ErrorAction SilentlyContinue } catch { }
}

# ---- input injection (in process: a child per event would be far too slow) ----
$winstaName = [WsmMirrorNative]::Name([WsmMirrorNative]::GetProcessWindowStation())
$desktopName = [WsmMirrorNative]::Name([WsmMirrorNative]::GetThreadDesktop([WsmMirrorNative]::GetCurrentThreadId()))
$screenDc = [WsmMirrorNative]::GetDC([IntPtr]::Zero)
$screenText = 'NONE (a session without a display device; per-window PrintWindow is used)'
if ($screenDc -ne [IntPtr]::Zero) {
    $screenText = 'ok'
    [void][WsmMirrorNative]::ReleaseDC([IntPtr]::Zero, $screenDc)
}
Log ("session: winsta=$winstaName desktop=$desktopName screenDC=$screenText")
$virtualKeys = @{
    'ENTER' = 0x0D; 'ESC' = 0x1B; 'TAB' = 0x09; 'SPACE' = 0x20; 'BACKSPACE' = 0x08; 'DELETE' = 0x2E
    'UP' = 0x26; 'DOWN' = 0x28; 'LEFT' = 0x25; 'RIGHT' = 0x27; 'HOME' = 0x24; 'END' = 0x23
    'PGUP' = 0x21; 'PGDN' = 0x22; 'INSERT' = 0x2D; 'CTRL' = 0x11; 'ALT' = 0x12; 'SHIFT' = 0x10
}
for ($i = 1; $i -le 24; $i++) { $virtualKeys["F$i"] = 0x6F + $i }

# Virtual keys that must carry KEYEVENTF_EXTENDEDKEY for SendInput.
$extendedKeys = @(0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x6F, 0xA2, 0xA3, 0xA4, 0xA5)

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
function Send-KeyEvent([int] $vk, [bool] $down) {
    $flags = [uint32]0
    if ($extendedKeys -contains $vk) { $flags = [WsmMirrorNative]::KeyeventfExtendedKey }
    if (-not $down) { $flags = $flags -bor [WsmMirrorNative]::KeyeventfKeyUp }
    [void][WsmMirrorNative]::KeyEvent([uint16]$vk, 0, $flags)
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
    # KEYEVENTF_UNICODE carries arbitrary UTF-16 (VkKeyScanW could only express what the
    # current layout maps to a key). Surrogate pairs go as two code units, which Windows
    # combines at the receiving end.
    foreach ($unit in $text.ToCharArray()) {
        $code = [uint16][int][char]$unit
        [void][WsmMirrorNative]::KeyEvent(0, $code, [WsmMirrorNative]::KeyeventfUnicode)
        [void][WsmMirrorNative]::KeyEvent(0, $code, ([WsmMirrorNative]::KeyeventfUnicode -bor [WsmMirrorNative]::KeyeventfKeyUp))
    }
}
function Invoke-InputLine([string] $line) {
    if ($line -like 'move *') {
        $xy = ($line -replace '^move\s+', '') -split ','
        Send-Move ([int]$xy[0]) ([int]$xy[1])
    } elseif ($line -like 'click *') {
        $xy = ($line -replace '^click\s+', '') -split ','
        $button = 1
        if ($xy.Count -gt 2) {
            $token = $xy[2].Trim().ToLowerInvariant()
            switch ($token) {
                'left' { $button = 1 }
                'right' { $button = 2 }
                'middle' { $button = 3 }
                default { $button = [int]$token }
            }
        }
        $times = 1
        if ($xy.Count -gt 3) { $times = [Math]::Max(1, [Math]::Min(4, [int]$xy[3])) }
        for ($n = 0; $n -lt $times; $n++) {
            Send-Click ([int]$xy[0]) ([int]$xy[1]) $button
        }
    } elseif ($line -like 'key *') {
        $parts = ($line -replace '^key\s+', '') -split '\s+'
        if ($parts.Count -ge 2) { Send-KeyEvent ([int]$parts[0]) ($parts[1] -ne 'up') }
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
    } elseif ($line -like 'launch *') {
        # Start a program in THIS session/desktop: the read side runs in the console
        # session, so anything it launches renders there - no cross-session creation
        # (which the silo denies) is needed.
        $rest = $line.Substring('launch '.Length)
        $exe = $rest
        $arguments = ''
        $space = $rest.IndexOf(' ')
        if ($space -gt 0) {
            $exe = $rest.Substring(0, $space)
            $arguments = $rest.Substring($space + 1)
        }
        try {
            if ($arguments) {
                Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Normal | Out-Null
            } else {
                Start-Process -FilePath $exe -WindowStyle Normal | Out-Null
            }
            Log ("launch '$rest' ok")
        } catch {
            Log ("launch '$rest' failed: " + $_.Exception.Message)
        }
    } elseif ($line -like 'resize *') {
        $rest = ($line -replace '^resize\s+', '') -split '\s+'
        $wh = $rest[1] -split ','
        $resized = [WsmMirrorWindows]::Resize([int64]$rest[0], [int]$wh[0], [int]$wh[1])
        Log ("resize 0x{0:X} -> {1}x{2} ok={3}" -f [int64]$rest[0], [int]$wh[0], [int]$wh[1], $resized)
    } elseif ($line -like 'wheel *') {
        $delta = [int]($line -replace '^wheel\s+', '')
        [WsmMirrorNative]::mouse_event(0x0800, 0, 0, [uint32]$delta, [IntPtr]::Zero)   # MOUSEEVENTF_WHEEL
        Log ("wheel $delta")
    } elseif ($line -like 'close *') {
        $target = [int64]($line -replace '^close\s+', '')
        $closed = [WsmMirrorWindows]::Close($target)
        Log ("close 0x{0:X} ok={1}" -f $target, $closed)
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
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr h, int command);
    [DllImport("kernel32.dll")] static extern int GetCurrentProcessId();
    public static List<Entry> List(string titleFilter) {
        var list = new List<Entry>();
        int self = GetCurrentProcessId();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
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
            int pid; GetWindowThreadProcessId(h, out pid);
            if (pid == self) return true;   // never mirror our own windows
            var e = new Entry();
            e.Id = h.ToInt64(); e.X = r.L; e.Y = r.T; e.W = w; e.H = hh; e.Title = sb.ToString();
            list.Add(e);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// Best effort: a window created in a service session may not carry WS_VISIBLE, and a hidden
    /// window yields nothing from PrintWindow. ShowWindow is unreliable across processes, but
    /// ShowWindowAsync and SetWindowPos(SWP_SHOWWINDOW) are made for exactly that.
    /// </summary>
    public static bool Show(long id) {
        var h = new IntPtr(id);
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004,
                   SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
        return ShowWindowAsync(h, 5) |
            SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
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

    // ---- capture strategies ------------------------------------------------------------------
    // A container window is a real window with real content, but two things are true there that
    // are not true on a normal desktop (measured, doc sections 23 and 24):
    //   * it never carries WS_VISIBLE, and
    //   * the service session has no display device, so neither PrintWindow nor a window-DC
    //     BitBlt has a painted surface to copy.
    // What still works is asking the window to paint itself into a DC we own, because that needs
    // neither visibility nor a surface. Which of the strategies does that varies with the app,
    // so capture is a MENU and the caller measures which entry produces ink instead of assuming
    // one - the old "PrintWindow only, and only for a visible window" version returned nothing at
    // all in a container.
    public const int StrategyPrintWindowFull = 0;
    public const int StrategyPrintWindow = 1;
    public const int StrategyWmPrint = 2;
    public const int StrategyWmPrintClient = 3;
    public const int StrategyChildSweep = 4;
    public const int StrategyRedrawBitBlt = 5;
    public const int StrategyWindowDcBitBlt = 6;

    public static readonly string[] StrategyNames = new string[] {
        "PrintWindow(PW_RENDERFULLCONTENT)", "PrintWindow(0)", "WM_PRINT",
        "WM_PRINTCLIENT", "child-sweep WM_PRINTCLIENT", "RedrawWindow+BitBlt", "windowDC+BitBlt"
    };

    const int WM_PRINT = 0x0317;
    const int WM_PRINTCLIENT = 0x0318;
    const uint PRF_NONCLIENT = 0x0002, PRF_CLIENT = 0x0004, PRF_ERASEBKGND = 0x0008,
               PRF_CHILDREN = 0x0010, PRF_OWNED = 0x0020;
    const uint RDW_INVALIDATE = 0x0001, RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;
    const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr region, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool IsWindow(IntPtr h);
    delegate bool ChildProc(IntPtr h, IntPtr lparam);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, ChildProc proc, IntPtr lparam);

    /// <summary>
    /// Paints one window into <paramref name="dc"/> with one strategy, with the DC's viewport
    /// shifted to the window's place on the desktop so a single desktop-sized DIB section covers
    /// every window. Never requires WS_VISIBLE.
    /// </summary>
    public static bool Paint(IntPtr dc, long id, int x, int y, int w, int h, int strategy) {
        var hwnd = new IntPtr(id);
        if (!IsWindow(hwnd)) return false;
        int saved = SaveDC(dc);
        try {
            SetViewportOrgEx(dc, x, y, IntPtr.Zero);
            switch (strategy) {
                case StrategyPrintWindowFull:
                    return PrintWindow(hwnd, dc, 0x2);            // PW_RENDERFULLCONTENT
                case StrategyPrintWindow:
                    return PrintWindow(hwnd, dc, 0x0);
                case StrategyWmPrint:
                    return Ask(hwnd, WM_PRINT, dc, PrintFlags());
                case StrategyWmPrintClient:
                    return Ask(hwnd, WM_PRINTCLIENT, dc, PrintFlags());
                case StrategyChildSweep:
                    return SweepChildren(hwnd, dc, x, y);
                case StrategyRedrawBitBlt:
                    RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RDW_INVALIDATE | RDW_ALLCHILDREN | RDW_UPDATENOW);
                    return BlitWindow(hwnd, dc, w, h);
                default:
                    return BlitWindow(hwnd, dc, w, h);
            }
        } finally { RestoreDC(dc, saved); }
    }

    /// <summary>Kept for the plain call site: strategy 0.</summary>
    public static bool Paint(IntPtr dc, long id, int x, int y, int w, int h) {
        return Paint(dc, id, x, y, w, h, StrategyPrintWindowFull);
    }

    /// <summary>PRF_* without PRF_CHECKVISIBLE: a hidden window must print anyway.</summary>
    static uint PrintFlags() {
        return PRF_NONCLIENT | PRF_CLIENT | PRF_ERASEBKGND | PRF_CHILDREN | PRF_OWNED;
    }

    static IntPtr FlagBits(uint flags) { return new IntPtr(unchecked((int)flags)); }

    /// <summary>SendMessageTimeout: a window that does not answer must not park the read side.</summary>
    static bool Ask(IntPtr hwnd, int msg, IntPtr dc, uint flags) {
        IntPtr result;
        return SendMessageTimeout(hwnd, msg, dc, FlagBits(flags), SMTO_ABORTIFHUNG, 2000, out result) != IntPtr.Zero;
    }

    /// <summary>
    /// Paints every descendant control on its own, at its own place. Last-resort strategy for
    /// frames whose own WM_PRINT handler does nothing (the controls below them still answer
    /// WM_PRINTCLIENT - that is how toolkits draw offscreen).
    /// </summary>
    static bool SweepChildren(IntPtr parent, IntPtr dc, int x, int y) {
        RECT parentRect; GetWindowRect(parent, out parentRect);
        bool any = false;
        EnumChildWindows(parent, delegate(IntPtr child, IntPtr lparam) {
            if (!IsWindowVisible(child) && (GetWindowLong(child, -20) & 0x00000080) != 0) return true;
            RECT r; if (!GetWindowRect(child, out r)) return true;
            int cw = r.R - r.L, ch = r.B - r.T;
            if (cw <= 0 || ch <= 0) return true;
            int saved = SaveDC(dc);
            try {
                SetViewportOrgEx(dc, x + (r.L - parentRect.L), y + (r.T - parentRect.T), IntPtr.Zero);
                IntPtr result;
                // Client-only: a child's frame is the parent's business, and PRF_CHECKVISIBLE is
                // deliberately absent.
                if (SendMessageTimeout(child, WM_PRINTCLIENT, dc, FlagBits(PRF_CLIENT | PRF_ERASEBKGND),
                        SMTO_ABORTIFHUNG, 2000, out result) != IntPtr.Zero) {
                    any = true;
                }
            } finally { RestoreDC(dc, saved); }
            return true;
        }, IntPtr.Zero);
        return any;
    }

    static bool BlitWindow(IntPtr hwnd, IntPtr dc, int w, int h) {
        var src = GetWindowDC(hwnd);
        if (src == IntPtr.Zero) return false;
        try { return BitBlt(dc, 0, 0, w, h, src, 0, 0, 0x00CC0020); }   // SRCCOPY
        finally { ReleaseDC(hwnd, src); }
    }

    /// <summary>Moves a window without resizing, restacking or activating it.</summary>
    public static bool Move(long id, int x, int y) {
        const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        return SetWindowPos(new IntPtr(id), IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Resizes a window without moving, restacking or activating it.</summary>
    public static bool Resize(long id, int w, int h) {
        const uint SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        return SetWindowPos(new IntPtr(id), IntPtr.Zero, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Asks a window to close; posted, so a hung window cannot park the read side.</summary>
    public static bool Close(long id) {
        return PostMessage(new IntPtr(id), WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    const int WM_CLOSE = 0x0010;
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool PostMessage(IntPtr h, int msg, IntPtr wparam, IntPtr lparam);

    /// <summary>Why the window list is (not) what we expect - written to the log once.</summary>
    public static string Diagnose() {
        var sb = new StringBuilder();
        int total = 0, visible = 0, titled = 0, sized = 0;
        var samples = new StringBuilder();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            total++;
            bool vis = IsWindowVisible(h);
            if (vis) visible++;
            var t = new StringBuilder(300);
            GetWindowTextW(h, t, t.Capacity);
            if (t.Length > 0) titled++;
            RECT r; GetWindowRect(h, out r);
            int w = r.R - r.L, hh = r.B - r.T;
            if (vis && t.Length > 0 && w >= 24 && hh >= 24) sized++;
            if (samples.Length < 600) {
                int pid; GetWindowThreadProcessId(h, out pid);
                samples.Append(string.Format("[0x{0:X} pid={1} vis={2} iconic={3} ex=0x{4:X} {5}x{6} \"{7}\"] ",
                    h.ToInt64(), pid, vis, IsIconic(h), GetWindowLong(h, -20), w, hh, t.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        sb.Append(string.Format("top-level={0} visible={1} titled={2} sized={3} :: {4}",
            total, visible, titled, sized, samples.ToString()));
        return sb.ToString();
    }

    /// <summary>A window's DC, so a memory DC can be derived where there is no screen DC.</summary>
    public static IntPtr WindowDc(long id) { return GetWindowDC(new IntPtr(id)); }

    /// <summary>Releases a DC obtained from <see cref="WindowDc"/>.</summary>
    public static void ReleaseWindowDc(long id, IntPtr dc) { ReleaseDC(new IntPtr(id), dc); }

    /// <summary>The handle of the first visible titled window, or 0.</summary>
    public static long FirstWindow(string titleFilter) {
        var list = List(titleFilter);
        return list.Count > 0 ? list[0].Id : 0L;
    }

    /// <summary>Brings a window to the foreground (a click on a mirrored window).</summary>
    public static bool Activate(long id) {
        const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002;
        return SetWindowPos(new IntPtr(id), new IntPtr(-1), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE);
    }
}
'@

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WsmMirrorDesktops {
    // Without this the names come back as single characters (classic ANSI/Unicode marshalling
    // mismatch), so every OpenWindowStation fails with ERROR_FILE_NOT_FOUND.
    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    public delegate bool NameProc(string name, IntPtr lparam);
    [DllImport("user32.dll", EntryPoint = "EnumWindowStationsW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumWindowStations(NameProc proc, IntPtr lparam);
    [DllImport("user32.dll", EntryPoint = "EnumDesktopsW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool EnumDesktops(IntPtr winsta, NameProc proc, IntPtr lparam);
    [DllImport("user32.dll", EntryPoint = "OpenWindowStationW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenWindowStationW(string name, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetProcessWindowStation(IntPtr winsta);
    [DllImport("user32.dll", EntryPoint = "OpenDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenDesktopW(string name, int flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")] static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] static extern IntPtr GetThreadDesktop(int threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder info, int len, out int needed);
    [DllImport("kernel32.dll")] static extern int GetCurrentThreadId();

    public static string Name(IntPtr handle) {
        if (handle == IntPtr.Zero) return "(null)";
        var sb = new StringBuilder(256); int needed;
        GetUserObjectInformationW(handle, 2, sb, sb.Capacity * 2, out needed);
        return sb.ToString();
    }

    public static string Current() {
        return Name(GetProcessWindowStation()) + @"\" + Name(GetThreadDesktop(GetCurrentThreadId()));
    }

    public static List<string> Stations() {
        var list = new List<string>();
        EnumWindowStations(delegate(string n, IntPtr l) { list.Add(n); return true; }, IntPtr.Zero);
        return list;
    }

    public static List<string> Desktops(IntPtr winsta) {
        var list = new List<string>();
        EnumDesktops(winsta, delegate(string n, IntPtr l) { list.Add(n); return true; }, IntPtr.Zero);
        return list;
    }

    public static IntPtr OpenStation(string name) {
        // Minimal rights: asking for WINSTA_ALL_ACCESS is denied (err 5) even for our own
        // station, while enumerate + read attributes is granted.
        return OpenWindowStationW(name, false, 0x0103);
    }

    public static IntPtr OpenDesk(string name) {
        const uint DESKTOP_READOBJECTS = 0x0001, DESKTOP_ENUMERATE = 0x0040;
        return OpenDesktopW(name, 0, false, DESKTOP_READOBJECTS | DESKTOP_ENUMERATE);
    }
    public static bool UseStation(IntPtr winsta) { return SetProcessWindowStation(winsta); }
    public static bool UseDesktop(IntPtr desktop) { return SetThreadDesktop(desktop); }
}
'@

# ---- attach to the desktop that actually has windows -------------------------------
# A helper process in a container does not necessarily inherit the same window station /
# desktop as the GUI processes of that session (measured: our own console window was the only
# window on the inherited desktop while notepad lived elsewhere). Window handles are
# per-desktop, so the read side has to BE on the desktop it captures.
$attached = ''
$bestCount = -1
$stations = [WsmMirrorDesktops]::Stations()
Log ("window stations visible in this session: " + $stations.Count + " [" + ($stations -join ', ') + "]")
foreach ($stationName in $stations) {
    $stationHandle = [WsmMirrorDesktops]::OpenStation($stationName)
    if ($stationHandle -eq [IntPtr]::Zero) {
        Log ("window station '$stationName': open failed (err " + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + ")")
        continue
    }
    if (-not [WsmMirrorDesktops]::UseStation($stationHandle)) {
        Log ("window station '$stationName': cannot attach (err " + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + ")")
        continue
    }
    $desktopNames = [WsmMirrorDesktops]::Desktops($stationHandle)
    Log ("station '$stationName' -> desktops: [" + ($desktopNames -join ', ') + "]")
    foreach ($desktopName in $desktopNames) {
        $desktopHandle = [WsmMirrorDesktops]::OpenDesk($desktopName)
        if ($desktopHandle -eq [IntPtr]::Zero) {
            Log ("  desktop '$desktopName': open failed (err " + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + ")")
            continue
        }
        if (-not [WsmMirrorDesktops]::UseDesktop($desktopHandle)) {
            Log ("  desktop '$desktopName': cannot attach (err " + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + ")")
            continue
        }
        $count = ([WsmMirrorWindows]::List($TitleFilter)).Count
        Log ("  candidate ${stationName}\$desktopName -> $count window(s)")
        if ($count -gt $bestCount) {
            $bestCount = $count
            $attached = "$stationName\$desktopName"
        }
    }
}
if ($bestCount -gt 0) {
    # Attach for real (re-open and switch), so every later call sees that desktop.
    foreach ($stationName in [WsmMirrorDesktops]::Stations()) {
        if ($attached -notlike ($stationName + '\*')) { continue }
        $stationHandle = [WsmMirrorDesktops]::OpenStation($stationName)
        if ($stationHandle -eq [IntPtr]::Zero) { continue }
        [void][WsmMirrorDesktops]::UseStation($stationHandle)
        $desktopName = $attached.Substring($stationName.Length + 1)
        $desktopHandle = [WsmMirrorDesktops]::OpenDesk($desktopName)
        if ($desktopHandle -ne [IntPtr]::Zero) { [void][WsmMirrorDesktops]::UseDesktop($desktopHandle) }
    }
    Log ("attached to $attached ($bestCount window(s)); now on " + [WsmMirrorDesktops]::Current())
} else {
    Log ("no desktop with windows found in this session; staying on " + [WsmMirrorDesktops]::Current())
}

# ---- shared mirror file ----
# Optionally start the program we are supposed to mirror: a child inherits this process's
# window station and desktop, which is the only reliable way to land on the same desktop as
# the GUI - inside a container, opening another window station is denied outright (measured:
# every OpenWindowStation fails with ERROR_ACCESS_DENIED, even for our own station).
if ($StartProgram) {
    try {
        # -WindowStyle Normal matters: this process's console is hidden, and a child started
        # without it inherits SW_HIDE - the window then exists but is never WS_VISIBLE, so it
        # is not something to mirror.
        Start-Process -FilePath $StartProgram -WindowStyle Normal | Out-Null
        Log ("started '$StartProgram' from the read side (same desktop as this process)")
        Start-Sleep -Seconds 3
    } catch {
        Log ("StartProgram '$StartProgram' failed: " + $_.Exception.Message)
    }
}
$stride = $Width * 4
$pixels = $stride * $Height
$tableOffset = 64 + $pixels
$dirtyOffset = $tableOffset + 8192
$titleOffset = $dirtyOffset + 3264
$capacity = $titleOffset + 16384
$mirrorPath = Join-Path $AgentDirectory $MirrorFile
$stopPath = Join-Path $AgentDirectory $StopFile

# The host picks the sharing mode before starting us; absent/unknown = file.
$mode = 'file'
$modePath = Join-Path $AgentDirectory 'shm-mode.txt'
if (Test-Path $modePath) {
    try { if ([IO.File]::ReadAllText($modePath) -match 'pagefile') { $mode = 'pagefile' } } catch { }
}

if (-not (Test-Path $AgentDirectory)) { New-Item -ItemType Directory -Path $AgentDirectory -Force | Out-Null }
if (Test-Path $mirrorPath) { Remove-Item $mirrorPath -Force -ErrorAction SilentlyContinue }

# In pagefile mode the .shm file carries only the header (the host's wait/adopt logic
# keeps working unchanged); the pixels live in a page-file-backed section.
$fileCapacity = $capacity
if ($mode -eq 'pagefile') { $fileCapacity = 4096 }

$file = [WsmMirrorNative]::CreateFileW($mirrorPath, [uint32]3221225472, [uint32]7, [IntPtr]::Zero, [uint32]2, [uint32]0x80, [IntPtr]::Zero)
if ($file -eq [IntPtr](-1)) { throw ("CreateFileW failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
$fileMapping = [WsmMirrorNative]::CreateFileMappingW($file, [IntPtr]::Zero, [uint32]4, 0, [uint32]$fileCapacity, $null)
if ($fileMapping -eq [IntPtr]::Zero) { throw ("CreateFileMappingW failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
$fileBase = [WsmMirrorNative]::MapViewOfFile($fileMapping, [uint32]0xF001F, 0, 0, [uint64]$fileCapacity)
if ($fileBase -eq [IntPtr]::Zero) { throw ("MapViewOfFile failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }

# $mapping/$base are where the header (live fields) and the pixels go; in file mode
# they ARE the file's mapping. In pagefile mode the file keeps a static header copy.
$marshal = [Runtime.InteropServices.Marshal]
$mapping = $fileMapping
$base = $fileBase
if ($mode -eq 'pagefile') {
    $mapping = [WsmMirrorNative]::CreateFileMappingW([IntPtr](-1), [IntPtr]::Zero, [uint32]4, 0, [uint32]$capacity, $null)
    if ($mapping -eq [IntPtr]::Zero) { throw ("page-file section failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
    $base = [WsmMirrorNative]::MapViewOfFile($mapping, [uint32]0xF001F, 0, 0, [uint64]$capacity)
    if ($base -eq [IntPtr]::Zero) { throw ("page-file view failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
    # Busy first, magic only inside the full header: a host that finds the section
    # early must never see it half-initialised.
    $marshal::WriteInt32($base, 32, 1)
}

$modeValue = 0
if ($mode -eq 'pagefile') { $modeValue = 1 }
$marshal::WriteInt32($base, 0, [int]0x4D4D5357)
$marshal::WriteInt32($base, 4, 2)
$marshal::WriteInt32($base, 8, $Width)
$marshal::WriteInt32($base, 12, $Height)
$marshal::WriteInt32($base, 16, $stride)
$marshal::WriteInt32($base, 20, $Fps)
$marshal::WriteInt64($base, 24, [int64]0)
$marshal::WriteInt32($base, 32, 0)
$marshal::WriteInt32($base, 36, $PID)
$marshal::WriteInt32($base, 40, 0)
$marshal::WriteInt64($base, 48, [int64]0)
$marshal::WriteInt32($base, 56, 0)   # dirtyCount
$marshal::WriteInt32($base, 60, $modeValue)

if ($mode -eq 'pagefile') {
    # Publish the static header file LAST: a valid header file is the host's signal
    # that the section is ready for the handle passthrough.
    $marshal::WriteInt32($fileBase, 0, [int]0x4D4D5357)
    $marshal::WriteInt32($fileBase, 4, 2)
    $marshal::WriteInt32($fileBase, 8, $Width)
    $marshal::WriteInt32($fileBase, 12, $Height)
    $marshal::WriteInt32($fileBase, 16, $stride)
    $marshal::WriteInt32($fileBase, 20, $Fps)
    $marshal::WriteInt32($fileBase, 36, $PID)
    $marshal::WriteInt32($fileBase, 60, $modeValue)
}

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
        IntPtr bits;
        return CreateCore(width, height, section, offset, out bits, out error);
    }

    /// <summary>
    /// Creates a top-down 32bpp DIB with <b>private</b> memory (no file mapping) and hands back
    /// its pixels. Measured: GDI in a container's service session accepts a paint into a section
    /// backed DIB and leaves it empty, but renders normally into a private one - so the read side
    /// paints here and moves the result across in one copy.
    /// </summary>
    public static IntPtr CreatePrivate(int width, int height, out IntPtr bits, out int error) {
        return CreateCore(width, height, IntPtr.Zero, 0, out bits, out error);
    }

    static IntPtr CreateCore(int width, int height, IntPtr section, uint offset, out IntPtr bits, out int error) {
        var info = new BITMAPINFO();
        info.header.biSize = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
        info.header.biWidth = width;
        info.header.biHeight = -height;   // top-down: the shared buffer is top-down BGRX
        info.header.biPlanes = 1;
        info.header.biBitCount = 32;
        info.header.biCompression = 0;    // BI_RGB
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
if ($memoryDc -eq [IntPtr]::Zero) {
    # A session whose desktop has no display device may refuse a screen-compatible DC
    # (the container's service session is exactly that). A window DC exists wherever
    # windows do, so derive the memory DC from one.
    Log 'CreateCompatibleDC(0) failed; deriving a memory DC from a window DC'
    $probeId = [WsmMirrorWindows]::FirstWindow($TitleFilter)
    if ($probeId -eq 0) { $probeId = [WsmMirrorWindows]::FirstWindow('') }
    if ($probeId -ne 0) {
        $windowDc = [WsmMirrorWindows]::WindowDc($probeId)
        if ($windowDc -ne [IntPtr]::Zero) {
            $memoryDc = [WsmMirrorDib]::CreateCompatibleDC($windowDc)
            [WsmMirrorWindows]::ReleaseWindowDc($probeId, $windowDc)
        }
    }
}
if ($memoryDc -eq [IntPtr]::Zero) {
    Log 'no memory DC could be created; giving up'
    throw 'CreateCompatibleDC failed'
}
[void][WsmMirrorDib]::SelectObject($memoryDc, $dib)
Log ("DIB section over shared memory ready (dc=$memoryDc dib=$dib)")

# A second DIB with PRIVATE pixels, for the sessions where GDI takes a paint call and leaves a
# section-backed DIB empty (measured in a container's service session, doc section 24). The two
# have the same geometry and stride, so moving a band between them is one contiguous copy.
$privateBits = [IntPtr]::Zero
$privateError = 0
$privateDib = [WsmMirrorDib]::CreatePrivate($Width, $Height, [ref]$privateBits, [ref]$privateError)
$privateDc = [IntPtr]::Zero
if ($privateDib -ne [IntPtr]::Zero) {
    $privateDc = [WsmMirrorDib]::CreateCompatibleDC([IntPtr]::Zero)
    if ($privateDc -eq [IntPtr]::Zero) { $privateDc = [WsmMirrorDib]::CreateCompatibleDC($memoryDc) }
    if ($privateDc -ne [IntPtr]::Zero) { [void][WsmMirrorDib]::SelectObject($privateDc, $privateDib) }
    Log ("private DIB ready (dc=$privateDc dib=$privateDib bits=$privateBits)")
} else {
    Log ("private DIB could not be created (err " + $privateError + "); only the shared DIB is available")
}

# The shadow buffer holds the previously published frame: each window's rect is compared
# against it block by block (WsmMirrorDirty) and only the changes are published as dirty
# rects, so a host reader can repaint just those.
$shadow = $marshal::AllocHGlobal($pixels)
$shadowRects = @{}

# The screen is read into a normal managed bitmap and moved into the shared DIB in one call:
# this is the capture path that was proven to work inside a container (doc section 13), while
# drawing directly into a DIB section over a file mapping failed there.
$captureBounds = New-Object Drawing.Rectangle(0, 0, $Width, $Height)
$captureSize = New-Object Drawing.Size($Width, $Height)
$captureBitmap = New-Object Drawing.Bitmap($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
$captureGraphics = [Drawing.Graphics]::FromImage($captureBitmap)

# ---- per-window capture strategy, chosen by measurement --------------------------
# Capture has two axes: WHERE the window paints (straight into the shared section-backed DIB, or
# into a private DIB that is then copied over) and HOW it is asked to (see WsmMirrorWindows).
# Both axes are measured, because the answers differ per session: on a desktop the first entry
# of the first row wins, in a container's service session every "paint into the shared DIB"
# entry reports success and leaves it empty. read.log keeps the whole matrix - it is the
# evidence for which combination this session actually supports.
$strategyNames = [WsmMirrorWindows]::StrategyNames
$strategyCount = $strategyNames.Count
$targetNames = @('shared-DIB', 'private-DIB+copy')
$chosenStrategy = -1
$chosenTarget = -1
$probed = $false

function Clear-Region([IntPtr] $target, [int] $x, [int] $y, [int] $w, [int] $h) {
    for ($ry = 0; $ry -lt $h; $ry++) {
        [void][WsmMirrorNative]::memset([IntPtr]::Add($target, ($y + $ry) * $stride + $x * 4), 0, [UIntPtr]([uint64]($w * 4)))
    }
}

# One contiguous move for a horizontal band: both DIBs are top-down with the same stride.
function Copy-Band([int] $y, [int] $h) {
    [WsmMirrorNative]::RtlMoveMemory(
        [IntPtr]::Add($pixelSection, $y * $stride),
        [IntPtr]::Add($privateBits, $y * $stride),
        [UIntPtr]([uint64]($h * $stride)))
}

function Measure-Ink([int] $x, [int] $y, [int] $w, [int] $h) {
    $nonBlack = 0; $sampled = 0
    for ($ry = 0; $ry -lt $h; $ry += 4) {
        for ($rx = 0; $rx -lt $w; $rx += 4) {
            $px = [IntPtr]::Add($pixelSection, (($y + $ry) * $stride) + (($x + $rx) * 4))
            $sampled++
            if ($marshal::ReadByte($px) -gt 8 -or $marshal::ReadByte($px, 1) -gt 8 -or $marshal::ReadByte($px, 2) -gt 8) { $nonBlack++ }
        }
    }
    if ($sampled -eq 0) { return 0.0 }
    return [Math]::Round(100 * $nonBlack / $sampled, 1)
}

Log ("read side started pid=$PID ${Width}x${Height} stride=$stride fps=$Fps seconds=$Seconds mirror=$mirrorPath mode=$mode")
Log ("input desktop: " + [WsmMirrorNative]::SwitchToInputDesktop())

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
        if ($sequence -eq 0) { Log ("diagnose: " + [WsmMirrorWindows]::Diagnose()) }
        $sequence++
        $count = [Math]::Min($windows.Count, [int](8192 / 40))
        $marshal::WriteInt32($base, 40, $count)

        # Capture once per pass, straight from the screen DC into the shared DIB section. The
        # container's service session does have a screen DC (measured), and this is a single
        # GDI copy - the per-window PrintWindow path stays as the fallback for sessions that
        # do not (where a hidden window also has no surface to read).
        $captured = $false
        try {
            $captureGraphics.CopyFromScreen(0, 0, 0, 0, $captureSize)
            $data = $captureBitmap.LockBits($captureBounds, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
            try {
                [WsmMirrorNative]::RtlMoveMemory($pixelSection, $data.Scan0, [UIntPtr]([uint64]($stride * $Height)))
                $captured = $true
            } finally { $captureBitmap.UnlockBits($data) }
        } catch {
            if ($sequence -eq 1) { Log ('screen capture failed: ' + $_.Exception.Message) }
        }
        if (-not $captured -and $sequence -eq 1) { Log 'falling back to per-window capture' }

        # Which combination actually produces pixels here? Answer it once, on the biggest window,
        # by zeroing the region and measuring what each (target, strategy) pair leaves behind.
        # GdiFlush matters: GDI batches, and without it the bits can still be empty when we look.
        # Asked once only: a session where nothing works must not spend every frame on this.
        if (-not $captured -and -not $probed -and $count -gt 0) {
            $probed = $true
            $probeIndex = 0; $probeArea = 0
            for ($i = 0; $i -lt $count; $i++) {
                $pw0 = [Math]::Min($Width, $windows[$i].X + $windows[$i].W) - [Math]::Max(0, $windows[$i].X)
                $ph0 = [Math]::Min($Height, $windows[$i].Y + $windows[$i].H) - [Math]::Max(0, $windows[$i].Y)
                if ($pw0 -ge 64 -and $ph0 -ge 64 -and ($pw0 * $ph0) -gt $probeArea) { $probeArea = $pw0 * $ph0; $probeIndex = $i }
            }
            $probe = $windows[$probeIndex]
            $px = [Math]::Max(0, $probe.X); $py = [Math]::Max(0, $probe.Y)
            $pw = [Math]::Min($Width, $probe.X + $probe.W) - $px
            $ph = [Math]::Min($Height, $probe.Y + $probe.H) - $py
            [void][WsmMirrorWindows]::Show($probe.Id)
            Start-Sleep -Milliseconds 300
            Log ("capture probe: window 0x{0:X} [{1}] rect=({2},{3} {4}x{5})" -f [int64]$probe.Id, $probe.Title, $px, $py, $pw, $ph)
            foreach ($t in 0, 1) {
                if ($t -eq 1 -and $privateDc -eq [IntPtr]::Zero) { continue }
                $targetDc = $memoryDc
                if ($t -eq 1) { $targetDc = $privateDc }
                for ($s = 0; $s -lt $strategyCount; $s++) {
                    Clear-Region $pixelSection $px $py $pw $ph
                    if ($t -eq 1) { Clear-Region $privateBits $px $py $pw $ph }
                    $ok = [WsmMirrorWindows]::Paint($targetDc, $probe.Id, $px, $py, $pw, $ph, $s)
                    [void][WsmMirrorNative]::GdiFlush()
                    if ($t -eq 1) { Copy-Band $py $ph }
                    $ink = Measure-Ink $px $py $pw $ph
                    Log ("  {0} + {1} -> call={2} ink={3}%" -f $targetNames[$t], $strategyNames[$s], $ok, $ink)
                    if ($ink -gt 0.0) { $chosenTarget = $t; $chosenStrategy = $s; break }
                }
                if ($chosenStrategy -ge 0) { break }
            }
            if ($chosenStrategy -ge 0) {
                Log ("capture chosen: " + $targetNames[$chosenTarget] + " + '" + $strategyNames[$chosenStrategy] + "'")
            } else {
                Log 'no capture strategy produced any ink for this window; publishing the window table only'
            }
            Clear-Region $pixelSection $px $py $pw $ph
            if ($t -eq 1) { Clear-Region $privateBits $px $py $pw $ph }
        }

        for ($i = 0; $i -lt $count; $i++) {
            $w = $windows[$i]
            $x = [Math]::Max(0, $w.X); $y = [Math]::Max(0, $w.Y)
            $right = [Math]::Min($Width, $w.X + $w.W); $bottom = [Math]::Min($Height, $w.Y + $w.H)
            $cw = $right - $x; $ch = $bottom - $y

            $painted = $captured
            if (-not $captured -and $chosenStrategy -ge 0 -and $cw -gt 0 -and $ch -gt 0) {
                if ($sequence -le 2) { [void][WsmMirrorWindows]::Show($w.Id) }
                $strategy = $chosenStrategy
                $paintDc = $memoryDc
                if ($strategy -lt 0) { $strategy = [WsmMirrorWindows]::StrategyPrintWindowFull }
                if ($chosenTarget -eq 1) { $paintDc = $privateDc }
                $painted = [WsmMirrorWindows]::Paint($paintDc, $w.Id, $x, $y, $cw, $ch, $strategy)
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
            if (-not $painted -and $chosenStrategy -ge 0) { Log ("paint call failed for 0x{0:X} (strategy '{1}')" -f [int64]$w.Id, $strategyNames[$chosenStrategy]) }
        }

        # Painting into the private DIB only counts once its pixels are in the shared buffer: one
        # contiguous move per pass covers every window, whatever painted them (doc section 24).
        if (-not $captured -and $chosenTarget -eq 1) {
            [void][WsmMirrorNative]::GdiFlush()
            [WsmMirrorNative]::RtlMoveMemory($pixelSection, $privateBits, [UIntPtr]([uint64]($stride * $Height)))
        }

        # Dirty rects: per window, the union of the 8x8 blocks that differ from the shadow
        # (last published) frame. A new, moved or resized window is fully dirty.
        if (-not $captured) { [void][WsmMirrorNative]::GdiFlush() }
        $dirtyCount = 0
        for ($i = 0; $i -lt $count -and $dirtyCount -lt 204; $i++) {
            $w = $windows[$i]
            $x = [Math]::Max(0, $w.X); $y = [Math]::Max(0, $w.Y)
            $cw = [Math]::Min($Width, $w.X + $w.W) - $x; $ch = [Math]::Min($Height, $w.Y + $w.H) - $y
            if ($cw -le 0 -or $ch -le 0) { continue }
            $rectKey = "$($w.X),$($w.Y),$($w.W),$($w.H)"
            $dx = 0; $dy = 0; $dw = 0; $dh = 0
            $changed = $false
            if (-not $shadowRects.ContainsKey($w.Id) -or $shadowRects[$w.Id] -ne $rectKey) {
                [WsmMirrorDirty]::Copy($shadow, $pixelSection, $stride, $x, $y, $cw, $ch)
                $dx = $x; $dy = $y; $dw = $cw; $dh = $ch
                $changed = $true
            } else {
                $changed = [WsmMirrorDirty]::Diff($pixelSection, $shadow, $stride, $x, $y, $cw, $ch, [ref]$dx, [ref]$dy, [ref]$dw, [ref]$dh)
            }
            $shadowRects[$w.Id] = $rectKey
            if ($changed) {
                $dirtyEntry = $dirtyOffset + $dirtyCount * 16
                $marshal::WriteInt32($base, $dirtyEntry, $dx)
                $marshal::WriteInt32($base, $dirtyEntry + 4, $dy)
                $marshal::WriteInt32($base, $dirtyEntry + 8, $dw)
                $marshal::WriteInt32($base, $dirtyEntry + 12, $dh)
                $dirtyCount++
            }
        }
        # Forget windows that left the table, so a re-created hwnd is fully dirty again.
        if ($shadowRects.Count -gt $count) {
            foreach ($shadowId in @($shadowRects.Keys)) {
                $stillThere = $false
                for ($i = 0; $i -lt $count; $i++) { if ($windows[$i].Id -eq $shadowId) { $stillThere = $true; break } }
                if (-not $stillThere) { $shadowRects.Remove($shadowId) }
            }
        }
        $marshal::WriteInt32($base, 56, $dirtyCount)

        # On the first pass, report how much of each window actually came back: this is the
        # container-side question ("does anything give content in a session with no display
        # device?"), and it is answerable from read.log alone. Measured after the copy above.
        if ($sequence -eq 1) {
            for ($i = 0; $i -lt $count; $i++) {
                $w = $windows[$i]
                $x = [Math]::Max(0, $w.X); $y = [Math]::Max(0, $w.Y)
                $cw = [Math]::Min($Width, $w.X + $w.W) - $x; $ch = [Math]::Min($Height, $w.Y + $w.H) - $y
                if ($cw -gt 16 -and $ch -gt 16) {
                    Log ("first paint 0x{0:X} rect=({1},{2} {3}x{4}) ink={5}%" -f [int64]$w.Id, $x, $y, $cw, $ch, (Measure-Ink $x $y $cw $ch))
                }
            }
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
try { $marshal::FreeHGlobal($shadow) } catch { }
[void][WsmMirrorNative]::UnmapViewOfFile($base)
[void][WsmMirrorNative]::CloseHandle($mapping)
if ($mode -eq 'pagefile') {
    [void][WsmMirrorNative]::UnmapViewOfFile($fileBase)
    [void][WsmMirrorNative]::CloseHandle($fileMapping)
}
[void][WsmMirrorNative]::CloseHandle($file)

}
catch {
    Log ("FATAL: " + $_.Exception.ToString())
    if (-not $AsShell) { throw }
    Log 'shell mode: restarting the read side in 2 seconds (the session must not lose its shell)'
    Start-Sleep -Seconds 2
    $relaunch = $true
}

} while ($relaunch)
