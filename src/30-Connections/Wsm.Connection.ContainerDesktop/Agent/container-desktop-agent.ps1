# container-desktop-agent.ps1 - the in-container half of the container desktop
# channel (see docs/investigation/13-container-desktop-and-rdp.md).
#
# It runs INSIDE a process-isolated container, on the session's active desktop,
# and does two things with no RDP and no sockets:
#   * captures that desktop into a memory-mapped frame file, and
#   * polls a one-file-per-command input queue and injects the commands with
#     user32 (SetCursorPos / mouse_event / keybd_event / VkKeyScan).
#
# The host maps the *same* file: a container's C:\ is the mounted scratch volume,
# so a plain file there is shared memory between the two silos.
#
# Frame file layout (little endian):
#   0  magic 'FSM1' (0x314D5346)   4  version (1)
#   8  width                       12 height
#   16 stride                      20 fps
#   24 seq (int64, ++ per frame)   32 busy (int32, 1 while writing)
#   36 writerPid (int32)           40 reserved
#   48 totalFrames (int64)
#   64.. pixels (height * stride bytes, top-down BGRX)
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern short VkKeyScanW(char c);
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
    foreach ($character in $text.ToCharArray()) {
        $scan = [WsmNative]::VkKeyScanW($character)
        if ($scan -eq -1) { continue }
        $key = [byte]($scan -band 0xff)
        $shift = ($scan -band 0x100) -ne 0
        $ctrl = ($scan -band 0x200) -ne 0
        if ($shift) { [WsmNative]::keybd_event(0x10, 0, 0, [IntPtr]::Zero) }
        if ($ctrl) { [WsmNative]::keybd_event(0x11, 0, 0, [IntPtr]::Zero) }
        [WsmNative]::keybd_event($key, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 15
        [WsmNative]::keybd_event($key, 0, 2, [IntPtr]::Zero); Start-Sleep -Milliseconds 15
        if ($ctrl) { [WsmNative]::keybd_event(0x11, 0, 2, [IntPtr]::Zero) }
        if ($shift) { [WsmNative]::keybd_event(0x10, 0, 2, [IntPtr]::Zero) }
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
    }
}

# ---- shared frame buffer ----
$stride = $Width * 4
$capacity = 64 + $stride * $Height
$framePath = Join-Path $AgentDirectory $FrameFile
$stopPath = Join-Path $AgentDirectory $StopFile

if (Test-Path $framePath) { Remove-Item $framePath -Force -ErrorAction SilentlyContinue }

$file = [WsmNative]::CreateFileW($framePath, [uint32]3221225472, [uint32]7, [IntPtr]::Zero, [uint32]2, [uint32]0x80, [IntPtr]::Zero)
if ($file -eq [IntPtr](-1)) { throw ("CreateFileW failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
$mapping = [WsmNative]::CreateFileMappingW($file, [IntPtr]::Zero, [uint32]4, 0, [uint32]$capacity, $null)
if ($mapping -eq [IntPtr]::Zero) { throw ("CreateFileMappingW failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
$base = [WsmNative]::MapViewOfFile($mapping, [uint32]0xF001F, 0, 0, [uint64]$capacity)
if ($base -eq [IntPtr]::Zero) { throw ("MapViewOfFile failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }

$marshal = [Runtime.InteropServices.Marshal]
$marshal::WriteInt32($base, 0, [int]0x314D5346)
$marshal::WriteInt32($base, 4, 1)
$marshal::WriteInt32($base, 8, $Width)
$marshal::WriteInt32($base, 12, $Height)
$marshal::WriteInt32($base, 16, $stride)
$marshal::WriteInt32($base, 20, $Fps)
$marshal::WriteInt64($base, 24, [int64]0)
$marshal::WriteInt32($base, 32, 0)
$marshal::WriteInt32($base, 36, $PID)
$marshal::WriteInt64($base, 48, [int64]0)

# Capture into a normal managed bitmap and move the pixels into the shared memory
# in one call: a Bitmap constructed *over* the mapped pointer renders black here.
$target = [IntPtr]::Add($base, 64)
$bitmap = New-Object Drawing.Bitmap($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$bounds = New-Object Drawing.Rectangle(0, 0, $Width, $Height)
$size = New-Object Drawing.Size($Width, $Height)

Log ("started pid=$PID ${Width}x${Height} stride=$stride fps=$Fps seconds=$Seconds frame=$framePath")

$frames = [int64]0
$seen = @{}
$deadline = (Get-Date).AddSeconds($Seconds)
$delay = [Math]::Max(1, [int](1000 / [Math]::Max(1, $Fps)))

while ((Get-Date) -lt $deadline) {
    if (Test-Path $stopPath) { Log 'stop marker seen'; break }

    try {
        $marshal::WriteInt32($base, 32, 1)
        $graphics.CopyFromScreen(0, 0, 0, 0, $size)
        $locked = $bitmap.LockBits($bounds, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
        try {
            if ($locked.Stride -eq $stride) {
                [WsmNative]::RtlMoveMemory($target, $locked.Scan0, [UIntPtr]([uint64]($stride * $Height)))
            } else {
                for ($y = 0; $y -lt $Height; $y++) {
                    [WsmNative]::RtlMoveMemory([IntPtr]::Add($target, $y * $stride), [IntPtr]::Add($locked.Scan0, $y * $locked.Stride), [UIntPtr]([uint64]$stride))
                }
            }
        } finally { $bitmap.UnlockBits($locked) }
        $frames++
        $marshal::WriteInt64($base, 48, $frames)
        $marshal::WriteInt64($base, 24, $frames)
        $marshal::WriteInt32($base, 32, 0)
    } catch {
        $marshal::WriteInt32($base, 32, 0)
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

    Start-Sleep -Milliseconds $delay
}

$marshal::WriteInt32($base, 32, 0)
Log ("exiting after frames=$frames")
try { $graphics.Dispose(); $bitmap.Dispose() } catch { }
[void][WsmNative]::UnmapViewOfFile($base)
[void][WsmNative]::CloseHandle($mapping)
[void][WsmNative]::CloseHandle($file)
