# container-desktop-agent.ps1 - the in-container half of the container desktop
# channel (see docs/investigation/13-container-desktop-and-rdp.md).
#
# It runs INSIDE a process-isolated container, on the session's active desktop,
# and does two things with no RDP and no sockets:
#   * captures that desktop into a memory-mapped frame file, and
#   * polls a one-file-per-command input queue and injects the commands with
#     user32 (SetCursorPos / mouse_event / SendInput).
#
# The host maps the *same* file: a container's C:\ is the mounted scratch volume,
# so a plain file there is shared memory between the two silos.
#
# Sharing mode (shm-mode.txt in the agent directory, written by the host before the
# agent starts; absent = 'file'):
#   file     - frame.shm carries header + pixels (the host maps the file).
#   pagefile - frame.shm carries ONLY the header (4 KB) and the pixels live in a
#              page-file-backed section this process creates; the host finds that
#              section by duplicating the handle out of this process (needs an
#              elevated reader; named sections do not cross the silo). The mode
#              field at offset 60 of the header tells the host which one it is.
#
# Frame file layout (little endian):
#   0  magic 'FSM1' (0x314D5346)   4  version (1)
#   8  width                       12 height
#   16 stride                      20 fps
#   24 seq (int64, ++ per frame)   32 busy (int32, 1 while writing)
#   36 writerPid (int32)           40 reserved
#   48 totalFrames (int64)         56 reserved
#   60 mode (int32: 0 = file, 1 = pagefile)
#   64.. pixels (height * stride bytes, top-down BGRX)
#
# Input queue (one or more commands per input-*.txt file, one per line):
#   move x,y                    SetCursorPos + a MOUSEEVENTF_MOVE
#   click x,y,btn[,count]       btn: left|right|middle (or 1|2|3); count is clamped to 4
#   key <vk> <down|up>          one SendInput key event (version 2 grammar, same as the
#                               window-mirror read side; extended keys are flagged)
#   keys CTRL+S                 legacy key chord via keybd_event (version 1 viewers)
#   text <string>               KEYEVENTF_UNICODE per UTF-16 code unit
#   wheel <delta>               MOUSEEVENTF_WHEEL
#   launch <exe> [args]         start a program in this session
#   clipboard <base64-utf8>     set the session clipboard (base64 so any text survives
#                               the file transport; retried, skipped while another app
#                               holds the clipboard open)
#
# Lifetime: the agent exits when agent-stop.txt appears, when -Seconds elapses (an outer
# safety bound), or when no viewer has been attached for 30 s. A viewer attaches by
# creating viewers\<guid>.lock next to this script's files and touching it every ~15 s;
# a lock not touched for 60 s belongs to a crashed viewer and is removed.
#
# Dynamic resolution (experimental): the viewer writes view-request.txt ('1280x720');
# the agent tries ChangeDisplaySettingsEx on the session's display, recreates the shared
# section on success (the host remaps when the header's width/height change) and answers
# in view-request.ack.txt ('applied WxH' or 'rejected WxH (status N)'). A failed change
# must never kill the loop - TSDDD is free to refuse.
#
# MUST be started with a visible console: a hidden/minimized process gets a black
# TSDDD framebuffer from CopyFromScreen (the real windows are still there - only
# the capture is blank).
param(
    [string] $AgentDirectory = 'C:\wsm',
    [string] $FrameFile = 'frame.shm',
    [string] $InputPrefix = 'input-',
    [string] $StopFile = 'agent-stop.txt',
    [int] $Width = 1024,
    [int] $Height = 768,
    [int] $Fps = 5,
    [int] $Seconds = 900
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WsmNative {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr sa, uint protect, uint maxHi, uint maxLo, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHi, uint offLo, ulong length);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern void RtlMoveMemory(IntPtr dest, IntPtr src, UIntPtr length);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool GetUserObjectInformationW(IntPtr obj, int index, System.Text.StringBuilder info, int length, out uint needed);

    /// <summary>
    /// Moves this thread to the session's ACTIVE desktop. CopyFromScreen only works
    /// there; on any other desktop it fails with ERROR_INVALID_HANDLE (measured in
    /// the container's services session, doc section 10.5).
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public class DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion; public ushort dmDriverVersion; public ushort dmSize; public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX; public int dmPositionY;
        public uint dmDisplayOrientation; public uint dmDisplayFixedOutput;
        public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels; public uint dmBitsPerPel; public uint dmPelsWidth; public uint dmPelsHeight;
        public uint dmDisplayFlags; public uint dmDisplayFrequency;
        public uint dmICMMethod; public uint dmICMIntent; public uint dmMediaType; public uint dmDitherType;
        public uint dmReserved1; public uint dmReserved2; public uint dmPanningWidth; public uint dmPanningHeight;
        public DEVMODE() { dmDeviceName = ""; dmFormName = ""; }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int ChangeDisplaySettingsEx(string device, DEVMODE devmode, IntPtr hwnd, uint flags, IntPtr lParam);

    /// <summary>Asks for a new primary-display size; 0 = DISP_CHANGE_SUCCESSFUL.</summary>
    public static int ChangeDisplayResolution(int width, int height) {
        var dm = new DEVMODE();
        dm.dmSize = (ushort)Marshal.SizeOf(typeof(DEVMODE));
        dm.dmFields = 0x00080000 | 0x00100000;   // DM_PELSWIDTH | DM_PELSHEIGHT
        dm.dmPelsWidth = (uint)width;
        dm.dmPelsHeight = (uint)height;
        return ChangeDisplaySettingsEx(null, dm, IntPtr.Zero, 0, IntPtr.Zero);
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr mem);

    /// <summary>
    /// Sets the session clipboard as Unicode text. OpenClipboard fails while another app
    /// holds the clipboard open, so it is retried for about half a second and then given
    /// up on - a lost paste must not stall the input loop.
    /// </summary>
    public static bool SetClipboardText(string text) {
        for (int attempt = 0; attempt < 10; attempt++) {
            if (!OpenClipboard(IntPtr.Zero)) { System.Threading.Thread.Sleep(50); continue; }
            try {
                if (!EmptyClipboard()) return false;
                var mem = GlobalAlloc(0x0042 /* GMEM_MOVEABLE | GMEM_ZEROINIT */, (UIntPtr)(uint)((text.Length + 1) * 2));
                if (mem == IntPtr.Zero) return false;
                var ptr = GlobalLock(mem);
                if (ptr == IntPtr.Zero) { GlobalFree(mem); return false; }
                var chars = text.ToCharArray();
                Marshal.Copy(chars, 0, ptr, chars.Length);
                Marshal.WriteInt16(ptr, chars.Length * 2, 0);
                GlobalUnlock(mem);
                // CF_UNICODETEXT; on success the clipboard owns the handle.
                return SetClipboardData(13, mem) != IntPtr.Zero;
            } finally { CloseClipboard(); }
        }
        return false;
    }
}
'@

$log = Join-Path $AgentDirectory 'agent.log'
function Log([string] $message) {
    $line = ('{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $message)
    try { Add-Content -Path $log -Value $line -ErrorAction SilentlyContinue } catch { }
}

# ---- input injection (in process: spawning a child per event is far too slow) ----
$virtualKeys = @{
    'ENTER' = 0x0D; 'ESC' = 0x1B; 'TAB' = 0x09; 'SPACE' = 0x20; 'BACKSPACE' = 0x08; 'DELETE' = 0x2E
    'UP' = 0x26; 'DOWN' = 0x28; 'LEFT' = 0x25; 'RIGHT' = 0x27; 'HOME' = 0x24; 'END' = 0x23
    'PGUP' = 0x21; 'PGDN' = 0x22; 'INSERT' = 0x2D; 'CTRL' = 0x11; 'ALT' = 0x12; 'SHIFT' = 0x10
}
for ($i = 1; $i -le 24; $i++) { $virtualKeys["F$i"] = 0x6F + $i }

# Virtual keys that must carry KEYEVENTF_EXTENDEDKEY for SendInput.
$extendedKeys = @(0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B, 0x5C, 0x6F, 0xA2, 0xA3, 0xA4, 0xA5)

function Send-Move([int] $x, [int] $y) {
    [void][WsmNative]::SetCursorPos($x, $y)
    [WsmNative]::mouse_event(0x0001, 0, 0, 0, [IntPtr]::Zero)
}
function Send-Click([int] $x, [int] $y, [int] $button) {
    Send-Move $x $y
    Start-Sleep -Milliseconds 40
    if ($button -eq 2) {
        [WsmNative]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30
        [WsmNative]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero)
    } elseif ($button -eq 3) {
        [WsmNative]::mouse_event(0x0020, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30
        [WsmNative]::mouse_event(0x0040, 0, 0, 0, [IntPtr]::Zero)
    } else {
        [WsmNative]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30
        [WsmNative]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    }
}
function Send-KeyEvent([int] $vk, [bool] $down) {
    $flags = [uint32]0
    if ($extendedKeys -contains $vk) { $flags = [WsmNative]::KeyeventfExtendedKey }
    if (-not $down) { $flags = $flags -bor [WsmNative]::KeyeventfKeyUp }
    [void][WsmNative]::KeyEvent([uint16]$vk, 0, $flags)
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
    foreach ($key in $keys) { [WsmNative]::keybd_event($key, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 15 }
    Start-Sleep -Milliseconds 40
    for ($i = $keys.Count - 1; $i -ge 0; $i--) {
        [WsmNative]::keybd_event($keys[$i], 0, 2, [IntPtr]::Zero); Start-Sleep -Milliseconds 15
    }
}
function Send-Text([string] $text) {
    # KEYEVENTF_UNICODE carries arbitrary UTF-16 (VkKeyScanW could only express what the
    # current layout maps to a key). Surrogate pairs go as two code units, which Windows
    # combines at the receiving end.
    foreach ($unit in $text.ToCharArray()) {
        $code = [uint16][int][char]$unit
        [void][WsmNative]::KeyEvent(0, $code, [WsmNative]::KeyeventfUnicode)
        [void][WsmNative]::KeyEvent(0, $code, ([WsmNative]::KeyeventfUnicode -bor [WsmNative]::KeyeventfKeyUp))
    }
}
function Invoke-Launch([string] $rest) {
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
    } elseif ($line -like 'wheel *') {
        $delta = [int]($line -replace '^wheel\s+', '')
        [WsmNative]::mouse_event(0x0800, 0, 0, [uint32]$delta, [IntPtr]::Zero)   # MOUSEEVENTF_WHEEL
    } elseif ($line -like 'launch *') {
        Invoke-Launch $line.Substring('launch '.Length)
    } elseif ($line -like 'clipboard *') {
        $payload = ($line -replace '^clipboard\s+', '').Trim()
        try {
            $text = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload))
            if ([WsmNative]::SetClipboardText($text)) { Log 'clipboard set' }
            else { Log 'clipboard not set (another app holds it open)' }
        } catch {
            Log ('clipboard failed: ' + $_.Exception.Message)
        }
    }
}

# ---- shared frame buffer (recreated in place by a successful view-request) ----
$framePath = Join-Path $AgentDirectory $FrameFile
$stopPath = Join-Path $AgentDirectory $StopFile
$requestPath = Join-Path $AgentDirectory 'view-request.txt'
$ackPath = Join-Path $AgentDirectory 'view-request.ack.txt'
$viewersPath = Join-Path $AgentDirectory 'viewers'

# The host picks the sharing mode before starting us; absent/unknown = file.
$mode = 'file'
$modePath = Join-Path $AgentDirectory 'shm-mode.txt'
if (Test-Path $modePath) {
    try { if ([IO.File]::ReadAllText($modePath) -match 'pagefile') { $mode = 'pagefile' } } catch { }
}

$marshal = [Runtime.InteropServices.Marshal]
$script:Stride = 0
$script:Frames = [int64]0
$script:File = [IntPtr](-1)
$script:Mapping = [IntPtr]::Zero
$script:Base = [IntPtr]::Zero
$script:PixelMapping = [IntPtr]::Zero
$script:PixelBase = [IntPtr]::Zero
$script:Target = [IntPtr]::Zero
$script:Bitmap = $null
$script:Graphics = $null
$script:Bounds = $null
$script:Size = $null

function Close-FrameSection {
    if ($script:Graphics) { try { $script:Graphics.Dispose() } catch { } $script:Graphics = $null }
    if ($script:Bitmap) { try { $script:Bitmap.Dispose() } catch { } $script:Bitmap = $null }
    if ($script:PixelBase -ne [IntPtr]::Zero) { [void][WsmNative]::UnmapViewOfFile($script:PixelBase); $script:PixelBase = [IntPtr]::Zero }
    if ($script:PixelMapping -ne [IntPtr]::Zero) { [void][WsmNative]::CloseHandle($script:PixelMapping); $script:PixelMapping = [IntPtr]::Zero }
    if ($script:Base -ne [IntPtr]::Zero) { [void][WsmNative]::UnmapViewOfFile($script:Base); $script:Base = [IntPtr]::Zero }
    if ($script:Mapping -ne [IntPtr]::Zero) { [void][WsmNative]::CloseHandle($script:Mapping); $script:Mapping = [IntPtr]::Zero }
    if ($script:File -ne [IntPtr](-1) -and $script:File -ne [IntPtr]::Zero) { [void][WsmNative]::CloseHandle($script:File); $script:File = [IntPtr](-1) }
}

function Write-Header([IntPtr] $target, [int] $w, [int] $h, [int] $modeValue, [bool] $live) {
    # Busy first and magic last: a host that catches the section mid-init must never
    # take a half-written header for a valid frame. In pagefile mode the file copy is
    # static (no busy/sequence), but the ordering costs nothing there either.
    if ($live) { $marshal::WriteInt32($target, 32, 1) }
    $marshal::WriteInt32($target, 4, 1)
    $marshal::WriteInt32($target, 8, $w)
    $marshal::WriteInt32($target, 12, $h)
    $marshal::WriteInt32($target, 16, $script:Stride)
    $marshal::WriteInt32($target, 20, $Fps)
    if ($live) { $marshal::WriteInt64($target, 24, $script:Frames) }
    $marshal::WriteInt32($target, 36, $PID)
    if ($live) { $marshal::WriteInt64($target, 48, $script:Frames) }
    $marshal::WriteInt32($target, 60, $modeValue)
    $marshal::WriteInt32($target, 0, [int]0x314D5346)
    if ($live) { $marshal::WriteInt32($target, 32, 0) }
}

function Open-FrameSection([int] $w, [int] $h) {
    Close-FrameSection
    $script:Width = $w
    $script:Height = $h
    $script:Stride = $w * 4
    $capacity = 64 + $script:Stride * $h

    # In pagefile mode the .shm file carries only the header (the host's wait/adopt logic
    # keeps working unchanged); the pixels live in a page-file-backed section.
    $fileCapacity = $capacity
    if ($mode -eq 'pagefile') { $fileCapacity = 4096 }

    # CREATE_ALWAYS: the same call serves the first open and a resize (truncate + regrow).
    $script:File = [WsmNative]::CreateFileW($framePath, [uint32]3221225472, [uint32]7, [IntPtr]::Zero, [uint32]2, [uint32]0x80, [IntPtr]::Zero)
    if ($script:File -eq [IntPtr](-1)) { throw ("CreateFileW failed: " + $marshal::GetLastWin32Error()) }
    $fileMapping = [WsmNative]::CreateFileMappingW($script:File, [IntPtr]::Zero, [uint32]4, 0, [uint32]$fileCapacity, $null)
    if ($fileMapping -eq [IntPtr]::Zero) { throw ("CreateFileMappingW failed: " + $marshal::GetLastWin32Error()) }
    $fileBase = [WsmNative]::MapViewOfFile($fileMapping, [uint32]0xF001F, 0, 0, [uint64]$fileCapacity)
    if ($fileBase -eq [IntPtr]::Zero) { throw ("MapViewOfFile failed: " + $marshal::GetLastWin32Error()) }

    # $PixelBase is where the header (live fields) and the pixels go; in file mode it IS
    # the file's mapping. In pagefile mode the file keeps a static copy of the header.
    $script:PixelMapping = $fileMapping
    $script:PixelBase = $fileBase
    if ($mode -eq 'pagefile') {
        $script:Mapping = $fileMapping
        $script:Base = $fileBase
        $script:PixelMapping = [WsmNative]::CreateFileMappingW([IntPtr](-1), [IntPtr]::Zero, [uint32]4, 0, [uint32]$capacity, $null)
        if ($script:PixelMapping -eq [IntPtr]::Zero) { throw ("page-file section failed: " + $marshal::GetLastWin32Error()) }
        $script:PixelBase = [WsmNative]::MapViewOfFile($script:PixelMapping, [uint32]0xF001F, 0, 0, [uint64]$capacity)
        if ($script:PixelBase -eq [IntPtr]::Zero) { throw ("page-file view failed: " + $marshal::GetLastWin32Error()) }
    }

    $modeValue = 0
    if ($mode -eq 'pagefile') { $modeValue = 1 }
    Write-Header $script:PixelBase $w $h $modeValue $true
    if ($mode -eq 'pagefile') {
        # Publish the static header file too: it is the host's signal for the handle
        # passthrough, and a resize changes the geometry it reports.
        Write-Header $script:Base $w $h $modeValue $false
    }

    # Capture into a normal managed bitmap and move the pixels into the shared memory
    # in one call: a Bitmap constructed *over* the mapped pointer renders black here.
    $script:Target = [IntPtr]::Add($script:PixelBase, 64)
    $script:Bitmap = New-Object Drawing.Bitmap($w, $h, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
    $script:Graphics = [Drawing.Graphics]::FromImage($script:Bitmap)
    $script:Bounds = New-Object Drawing.Rectangle(0, 0, $w, $h)
    $script:Size = New-Object Drawing.Size($w, $h)
    Log ("frame section ready ${w}x${h} stride=$($script:Stride) mode=$mode")
}

# ---- dynamic resolution (experimental) ----
function Invoke-ViewRequest([string] $spec) {
    if ($spec -notmatch '^(\d{3,5})x(\d{3,5})$') { return }
    $reqW = [int]$Matches[1]
    $reqH = [int]$Matches[2]
    if ($reqW -eq $Width -and $reqH -eq $Height) {
        try { [IO.File]::WriteAllText($ackPath, "applied ${reqW}x${reqH}") } catch { }
        return
    }
    $status = -1
    try {
        $status = [WsmNative]::ChangeDisplayResolution($reqW, $reqH)
        if ($status -eq 0) {
            Start-Sleep -Milliseconds 300
            if ([WsmNative]::GetSystemMetrics(0) -eq $reqW -and [WsmNative]::GetSystemMetrics(1) -eq $reqH) {
                Open-FrameSection $reqW $reqH
                [IO.File]::WriteAllText($ackPath, "applied ${reqW}x${reqH}")
                Log ("view-request applied ${reqW}x${reqH}")
                return
            }
            $status = -100   # the call claimed success but the session kept its size
        }
    } catch {
        Log ('view-request failed: ' + $_.Exception.Message)
    }
    try { [IO.File]::WriteAllText($ackPath, ("rejected {0}x{1} (status {2})" -f $reqW, $reqH, $status)) } catch { }
    Log ("view-request rejected ${reqW}x${reqH} status=$status")
}

# ---- viewer-count lifetime ----
# A live lock (viewers\<guid>.lock touched within the last 60 s) keeps the agent alive;
# stale locks belong to crashed viewers and are removed.
$lastViewerSeen = Get-Date
function Get-LiveViewerCount {
    $live = 0
    if (Test-Path $viewersPath) {
        $now = Get-Date
        foreach ($lock in @(Get-ChildItem -Path $viewersPath -Filter '*.lock' -ErrorAction SilentlyContinue)) {
            if (($now - $lock.LastWriteTimeUtc).TotalSeconds -le 60) {
                $live++
            } else {
                Remove-Item $lock.FullName -Force -ErrorAction SilentlyContinue
            }
        }
    }
    return $live
}

Open-FrameSection $Width $Height
Log ("started pid=$PID ${Width}x${Height} stride=$($script:Stride) fps=$Fps seconds=$Seconds frame=$framePath mode=$mode")
Log ("input desktop: " + [WsmNative]::SwitchToInputDesktop())

$seen = @{}
$deadline = (Get-Date).AddSeconds($Seconds)
$delay = [Math]::Max(1, [int](1000 / [Math]::Max(1, $Fps)))
$nextHousekeeping = Get-Date

while ((Get-Date) -lt $deadline) {
    if (Test-Path $stopPath) { Log 'stop marker seen'; break }

    try {
        $marshal::WriteInt32($PixelBase, 32, 1)
        $Graphics.CopyFromScreen(0, 0, 0, 0, $Size)
        $locked = $Bitmap.LockBits($Bounds, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
        try {
            if ($locked.Stride -eq $Stride) {
                [WsmNative]::RtlMoveMemory($Target, $locked.Scan0, [UIntPtr]([uint64]($Stride * $Height)))
            } else {
                for ($y = 0; $y -lt $Height; $y++) {
                    [WsmNative]::RtlMoveMemory([IntPtr]::Add($Target, $y * $Stride), [IntPtr]::Add($locked.Scan0, $y * $locked.Stride), [UIntPtr]([uint64]$Stride))
                }
            }
        } finally { $Bitmap.UnlockBits($locked) }
        $script:Frames++
        $marshal::WriteInt64($PixelBase, 48, $script:Frames)
        $marshal::WriteInt64($PixelBase, 24, $script:Frames)
        $marshal::WriteInt32($PixelBase, 32, 0)
    } catch {
        $marshal::WriteInt32($PixelBase, 32, 0)
        Log ('capture failed: ' + $_.Exception.Message)
        Start-Sleep -Seconds 1
        continue
    }

    # Host input: process every not-yet-seen queue file in order.
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
            if (-not $command) { continue }
            Log ('input ' + $fileInfo.Name + ' -> ' + (($command -replace "`r?`n", ' | ').Trim()))
            foreach ($line in ($command -split "`r?`n")) {
                $trimmed = $line.Trim()
                if ($trimmed -eq '') { continue }
                try { Invoke-InputLine $trimmed } catch { Log ('input failed: ' + $_.Exception.Message) }
            }
        }
    } catch {
        Log ('input queue failed: ' + $_.Exception.Message)
    }

    # Once a second: the resolution handshake and the viewer-count lifetime.
    if ((Get-Date) -ge $nextHousekeeping) {
        $nextHousekeeping = (Get-Date).AddSeconds(1)

        try {
            if (Test-Path $requestPath) {
                $spec = ([IO.File]::ReadAllText($requestPath)).Trim()
                Remove-Item $requestPath -Force -ErrorAction SilentlyContinue
                Invoke-ViewRequest $spec
            }
        } catch {
            Log ('view-request check failed: ' + $_.Exception.Message)
        }

        if ((Get-LiveViewerCount) -gt 0) {
            $lastViewerSeen = Get-Date
        } elseif (((Get-Date) - $lastViewerSeen).TotalSeconds -gt 30) {
            Log 'no live viewer for 30s; exiting'
            break
        }
    }

    Start-Sleep -Milliseconds $delay
}

$marshal::WriteInt32($PixelBase, 32, 0)
Log ("exiting after frames=$($script:Frames)")
Close-FrameSection
