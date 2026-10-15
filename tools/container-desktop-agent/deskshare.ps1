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
$lastInbox = ''
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

    try {
        $dir = Split-Path -Path $Inbox -Parent
        $candidates = @(Get-ChildItem -Path $dir -Filter 'inbox-*.txt' -ErrorAction SilentlyContinue | Sort-Object Name)
        if ($candidates.Count -gt 0) {
            $newest = $candidates[$candidates.Count - 1]
            if ($newest.Name -ne $lastInbox) {
                $cmd = ''
                $ifs = $null
                try {
                    $ifs = [IO.File]::Open($newest.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                    $sr = New-Object IO.StreamReader($ifs)
                    $cmd = $sr.ReadToEnd()
                    $sr.Dispose()
                }
                catch { }
                finally { if ($ifs) { $ifs.Dispose() } }
                if ($cmd -and $cmd.Trim() -ne '') {
                    $lastInbox = $newest.Name
                    Log ('inbox ' + $newest.Name + ' -> injecting: ' + (($cmd -replace "`r?`n", ' | ').Trim()))
                    $scratch = 'C:\wsm\inbox-run.txt'
                    Set-Content -Path $scratch -Value $cmd -Encoding ASCII
                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $DeskInput -Action script -TextFile $scratch -Log C:\wsm\inbox-run.log 2>&1 | Out-Null
                }
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
