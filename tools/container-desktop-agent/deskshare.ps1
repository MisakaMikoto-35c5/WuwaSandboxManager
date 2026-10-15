# deskshare.ps1 - present a container desktop to the HOST through shared memory.
#
# Why this shape (all measured - see docs/investigation/13-container-desktop-and-rdp.md):
#   * The container has NO DWM (DwmIsCompositionEnabled = False, no dwm.exe) and NO
#     usable D3D device (D3D11CreateDevice HARDWARE *and* WARP both fail with
#     0x887A0004), so WGC / DwmGetDxSharedSurface / shared GPU textures are not
#     available: only pixels can be shared.
#   * Named objects do NOT cross the silo boundary (the container has its own
#     BaseNamedObjects) -> rendezvous must be by file, not by name.
#   * The container's C:\ IS a raw volume the host can open, so a plain file in it
#     *is* shared memory: both sides map the same file object and cached pages.
#
# So the frame buffer lives in one file, mapped with Win32 directly (the .NET
# MemoryMappedFile overloads are awkward from PowerShell and take restrictive
# file sharing):
#     container  C:\wsm\frame.shm
#     host       \\?\Volume{guid}\wsm\frame.shm
#
# Frame file layout (little endian):
#   0   magic 'FSM1' (0x314D5346)    4  version (1)
#   8   width                        12 height
#   16  stride                       20 format (1 = 32bpp BGRX)
#   24  seq (int64, bumped per frame)
#   32  busy (int32, 1 while writing)
#   36  writerPid (int32)            40 fps (int32)
#   48  totalFrames (int64)
#   64.. pixels (height * stride bytes, top-down)
#
# Run it *inside* the container, on the session's active desktop, like deskcap.ps1
# (GDI only captures the desktop the calling thread is attached to).
#
# Input comes back through one *new* file per command, inbox-<timestamp>.txt, in
# the image layer (the host may write there without elevation; a rewritten single
# file would be locked by the container's filesystem after the first read).
param(
    [string] $Shm = 'C:\wsm\frame.shm',
    [string] $Inbox = 'C:\wsm\inbox.txt',
    [int] $Width = 1024,
    [int] $Height = 768,
    [int] $Fps = 5,
    [int] $Seconds = 120,
    [string] $Log = 'C:\wsm\deskshare.log',
    [string] $DeskInput = 'C:\wsm\deskinput.ps1'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShmWin32 {
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

$GENERIC_RW = [uint32]3221225472   # 0xC0000000: PS 5.1 parses that literal as a negative Int32
$SHARE_ALL = [uint32]7             # READ | WRITE | DELETE
$CREATE_ALWAYS = [uint32]2
$ATTR_NORMAL = [uint32]0x80
$PAGE_READWRITE = [uint32]0x04
$FILE_MAP_ALL = [uint32]0xF001F

function Log([string] $m) {
    $line = ('{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $m)
    Write-Output $line
    try { Add-Content -Path $Log -Value $line -ErrorAction SilentlyContinue } catch { }
}

# ---- input injection, in process (spawning deskinput.ps1 per event is far too
# slow for interactive use: each child costs 1-3 s in this container) ----
$VK = @{
    'ENTER' = 0x0D; 'ESC' = 0x1B; 'TAB' = 0x09; 'SPACE' = 0x20; 'BACKSPACE' = 0x08; 'DELETE' = 0x2E
    'UP' = 0x26; 'DOWN' = 0x28; 'LEFT' = 0x25; 'RIGHT' = 0x27; 'HOME' = 0x24; 'END' = 0x23
    'PGUP' = 0x21; 'PGDN' = 0x22; 'INSERT' = 0x2D; 'CTRL' = 0x11; 'ALT' = 0x12; 'SHIFT' = 0x10
}
for ($i = 1; $i -le 24; $i++) { $VK["F$i"] = 0x6F + $i }

function Send-Move([int] $x, [int] $y) {
    [void][ShmWin32]::SetCursorPos($x, $y)
    [ShmWin32]::mouse_event(0x0001, 0, 0, 0, [IntPtr]::Zero)
}
function Send-Click([int] $x, [int] $y, [int] $b) {
    Send-Move $x $y
    Start-Sleep -Milliseconds 40
    if ($b -eq 2) {
        [ShmWin32]::mouse_event(0x0008, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30
        [ShmWin32]::mouse_event(0x0010, 0, 0, 0, [IntPtr]::Zero)
    }
    elseif ($b -eq 3) {
        [ShmWin32]::mouse_event(0x0020, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30
        [ShmWin32]::mouse_event(0x0040, 0, 0, 0, [IntPtr]::Zero)
    }
    else {
        [ShmWin32]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30
        [ShmWin32]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    }
}
function Send-Keys([string] $spec) {
    $vks = @()
    foreach ($p in ($spec -split '\+')) {
        $t = $p.Trim().ToUpperInvariant()
        if ($t.StartsWith('VK_')) { $t = $t.Substring(3) }
        if ($VK.ContainsKey($t)) { $vks += [byte]$VK[$t] }
        elseif ($t.Length -eq 1 -and $t -match '^[A-Z0-9]$') { $vks += [byte][char]$t }
        elseif ($t -match '^0X[0-9A-F]+$') { $vks += [byte][Convert]::ToInt32($t.Substring(2), 16) }
    }
    if ($vks.Count -eq 0) { return }
    foreach ($v in $vks) { [ShmWin32]::keybd_event($v, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 15 }
    Start-Sleep -Milliseconds 40
    for ($i = $vks.Count - 1; $i -ge 0; $i--) { [ShmWin32]::keybd_event($vks[$i], 0, 2, [IntPtr]::Zero); Start-Sleep -Milliseconds 15 }
}
function Send-Text([string] $s) {
    foreach ($c in $s.ToCharArray()) {
        $r = [ShmWin32]::VkKeyScanW($c)
        if ($r -eq -1) { continue }
        $vk = [byte]($r -band 0xff)
        $shift = ($r -band 0x100) -ne 0
        $ctrl = ($r -band 0x200) -ne 0
        if ($shift) { [ShmWin32]::keybd_event(0x10, 0, 0, [IntPtr]::Zero) }
        if ($ctrl) { [ShmWin32]::keybd_event(0x11, 0, 0, [IntPtr]::Zero) }
        [ShmWin32]::keybd_event($vk, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 15
        [ShmWin32]::keybd_event($vk, 0, 2, [IntPtr]::Zero); Start-Sleep -Milliseconds 15
        if ($ctrl) { [ShmWin32]::keybd_event(0x11, 0, 2, [IntPtr]::Zero) }
        if ($shift) { [ShmWin32]::keybd_event(0x10, 0, 2, [IntPtr]::Zero) }
    }
}
function Invoke-InputLine([string] $l) {
    if ($l -like 'move *') {
        $xy = ($l -replace '^move\s+', '') -split ','
        Send-Move ([int]$xy[0]) ([int]$xy[1])
    }
    elseif ($l -like 'click *') {
        $xy = ($l -replace '^click\s+', '') -split ','
        $b = 1; if ($xy.Count -gt 2) { $b = [int]$xy[2] }
        Send-Click ([int]$xy[0]) ([int]$xy[1]) $b
    }
    elseif ($l -like 'keys *') { Send-Keys ($l -replace '^keys\s+', '') }
    elseif ($l -like 'text *') { Send-Text $l.Substring('text '.Length) }
}

$stride = $Width * 4
$capacity = 64 + $stride * $Height
Log ("start pid=$PID shm=$Shm ${Width}x${Height} stride=$stride cap=$capacity fps=$Fps seconds=$Seconds")

if (Test-Path $Shm) { Remove-Item $Shm -Force -ErrorAction SilentlyContinue }

$hFile = [ShmWin32]::CreateFileW($Shm, $GENERIC_RW, $SHARE_ALL, [IntPtr]::Zero, $CREATE_ALWAYS, $ATTR_NORMAL, [IntPtr]::Zero)
if ($hFile -eq [IntPtr](-1)) { throw ("CreateFileW failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
$hMap = [ShmWin32]::CreateFileMappingW($hFile, [IntPtr]::Zero, $PAGE_READWRITE, 0, [uint32]$capacity, $null)
if ($hMap -eq [IntPtr]::Zero) { throw ("CreateFileMappingW failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
$base = [ShmWin32]::MapViewOfFile($hMap, $FILE_MAP_ALL, 0, 0, [uint64]$capacity)
if ($base -eq [IntPtr]::Zero) { throw ("MapViewOfFile failed: " + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }

$m = [Runtime.InteropServices.Marshal]
$m::WriteInt32($base, 0, [int]0x314D5346)
$m::WriteInt32($base, 4, 1)
$m::WriteInt32($base, 8, $Width)
$m::WriteInt32($base, 12, $Height)
$m::WriteInt32($base, 16, $stride)
$m::WriteInt32($base, 20, 1)
$m::WriteInt64($base, 24, [int64]0)
$m::WriteInt32($base, 32, 0)
$m::WriteInt32($base, 36, $PID)
$m::WriteInt32($base, 40, $Fps)
$m::WriteInt64($base, 48, [int64]0)

# Capture into a normal managed bitmap and move the pixels into the shared memory
# in one call. A Bitmap constructed *over* the mapped pointer renders black here
# (CopyFromScreen silently no-ops into it), so don't do that.
$pixels = [IntPtr]::Add($base, 64)
$bmp = New-Object Drawing.Bitmap($Width, $Height, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
$gfx = [Drawing.Graphics]::FromImage($bmp)
$rect = New-Object Drawing.Rectangle(0, 0, $Width, $Height)
$size = New-Object Drawing.Size($Width, $Height)
Log 'mapped; capturing'

$frames = [int64]0
$seen = @{}
$deadline = (Get-Date).AddSeconds($Seconds)
$delay = [Math]::Max(1, [int](1000 / [Math]::Max(1, $Fps)))

while ((Get-Date) -lt $deadline) {
    try {
        $m::WriteInt32($base, 32, 1)
        $gfx.CopyFromScreen(0, 0, 0, 0, $size)
        $data = $bmp.LockBits($rect, [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppRgb)
        try {
            if ($data.Stride -eq $stride) {
                [ShmWin32]::RtlMoveMemory($pixels, $data.Scan0, [UIntPtr]([uint64]($stride * $Height)))
            }
            else {
                for ($y = 0; $y -lt $Height; $y++) {
                    [ShmWin32]::RtlMoveMemory([IntPtr]::Add($pixels, $y * $stride), [IntPtr]::Add($data.Scan0, $y * $data.Stride), [UIntPtr]([uint64]$stride))
                }
            }
        }
        finally { $bmp.UnlockBits($data) }
        $frames++
        $m::WriteInt64($base, 48, $frames)
        $m::WriteInt64($base, 24, $frames)
        $m::WriteInt32($base, 32, 0)
    }
    catch {
        Log ('capture failed: ' + $_.Exception.Message)
        $m::WriteInt32($base, 32, 0)
        Start-Sleep -Seconds 1
        continue
    }

    if (($frames % 20) -eq 0) { Log ("frames=$frames foreground=" + (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $DeskInput -Action info -Log C:\wsm\deskshare-info.log 2>&1 | Select-String 'foreground=' | Select-Object -First 1)) }

    # Input from the host: process every not-yet-seen inbox-*.txt in order.
    # (One file per command because the container's filesystem locks a layer file
    # once it has been read, so the host cannot rewrite a single inbox.)
    try {
        $dir = Split-Path -Path $Inbox -Parent
        $candidates = @(Get-ChildItem -Path $dir -Filter 'inbox-*.txt' -ErrorAction SilentlyContinue | Sort-Object Name)
        foreach ($f in $candidates) {
            if ($seen.ContainsKey($f.Name)) { continue }
            $seen[$f.Name] = $true
            $cmd = ''
            $ifs = $null
            try {
                $ifs = [IO.File]::Open($f.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                $sr = New-Object IO.StreamReader($ifs)
                $cmd = $sr.ReadToEnd()
                $sr.Dispose()
            }
            catch { }
            finally { if ($ifs) { $ifs.Dispose() } }
            if (-not $cmd) { continue }
            Log ('inbox ' + $f.Name + ' -> ' + (($cmd -replace "`r?`n", ' | ').Trim()))
            foreach ($line in ($cmd -split "`r?`n")) {
                $l = $line.Trim()
                if ($l -eq '') { continue }
                try { Invoke-InputLine $l } catch { Log ('input failed: ' + $_.Exception.Message) }
            }
        }
    }
    catch { Log ('inbox failed: ' + $_.Exception.Message) }

    Start-Sleep -Milliseconds $delay
}

$m::WriteInt32($base, 32, 0)
Log ("done frames=$frames")
try { $gfx.Dispose(); $bmp.Dispose() } catch { }
[void][ShmWin32]::UnmapViewOfFile($base)
[void][ShmWin32]::CloseHandle($hMap)
[void][ShmWin32]::CloseHandle($hFile)
Log 'closed'
