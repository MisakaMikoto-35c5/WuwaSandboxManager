# deskread.ps1 (HOST side) - read a frame out of the container's shared frame file
# and (a) report its header, (b) save a PNG so the pixel path can be inspected.
#
# The container's C:\ is a raw volume on this machine (it has no drive letter), so
# the host maps the exact same file object the container writes:
#     container  C:\wsm\frame.shm
#     host       \\?\Volume{guid}\wsm\frame.shm
#
#   powershell -File deskread.ps1 -Out frame.png
param(
    [string] $Mount,
    [string] $Rel = 'wsm\frame.shm',
    [string] $Out = 'frame.png',
    [int] $WaitSeconds = 60
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShmRead {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr sa, uint protect, uint maxHi, uint maxLo, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHi, uint offLo, ulong length);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
}
'@
$m = [Runtime.InteropServices.Marshal]

if (-not $Mount) {
    $mountFile = "$env:TEMP\opencode\wsmdiag\dd\mount.txt"
    $stateFile = "$env:TEMP\opencode\wsmdiag\dd\state.json"
    if (Test-Path $mountFile) { $Mount = (Get-Content $mountFile -Raw).Trim() }
    if (-not $Mount -and (Test-Path $stateFile)) { $Mount = (Get-Content $stateFile | ConvertFrom-Json).mount }
}
if (-not $Mount) { throw 'no mount path (pass -Mount \\?\Volume{...})' }
$path = $Mount.TrimEnd('\') + '\' + $Rel
Write-Output ('shared frame file: ' + $path)

$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline) {
    if ((Test-Path $path) -and ((Get-Item $path).Length -gt 64)) { break }
    Start-Sleep -Milliseconds 500
}
if (-not (Test-Path $path)) { throw "frame file never appeared: $path" }

$GENERIC_READ = [uint32]2147483648  # 0x80000000
$SHARE_ALL = [uint32]7
$OPEN_EXISTING = [uint32]3
$ATTR_NORMAL = [uint32]0x80
$PAGE_READONLY = [uint32]0x02
$FILE_MAP_READ = [uint32]0x04

$hFile = [ShmRead]::CreateFileW($path, $GENERIC_READ, $SHARE_ALL, [IntPtr]::Zero, $OPEN_EXISTING, $ATTR_NORMAL, [IntPtr]::Zero)
if ($hFile -eq [IntPtr](-1)) { throw ('CreateFileW failed: ' + $m::GetLastWin32Error()) }
$hMap = [ShmRead]::CreateFileMappingW($hFile, [IntPtr]::Zero, $PAGE_READONLY, 0, 0, $null)
if ($hMap -eq [IntPtr]::Zero) { throw ('CreateFileMappingW failed: ' + $m::GetLastWin32Error()) }
$base = [ShmRead]::MapViewOfFile($hMap, $FILE_MAP_READ, 0, 0, [uint64]0)
if ($base -eq [IntPtr]::Zero) { throw ('MapViewOfFile failed: ' + $m::GetLastWin32Error()) }

try {
    $magic = $m::ReadInt32($base, 0)
    $w = $m::ReadInt32($base, 8)
    $h = $m::ReadInt32($base, 12)
    $stride = $m::ReadInt32($base, 16)
    $busy = $m::ReadInt32($base, 32)
    $writerPid = $m::ReadInt32($base, 36)
    $fps = $m::ReadInt32($base, 40)
    $frames = $m::ReadInt64($base, 48)
    Write-Output ("header: magic=0x{0:X8} {1}x{2} stride={3} busy={4} writerPid={5} fps={6} frames={7} created={8}" -f `
            $magic, $w, $h, $stride, $busy, $writerPid, $fps, $frames, (Get-Item $path).CreationTime)
    if ($magic -ne 0x314D5346) { Write-Output '  WARNING: not an FSM1 frame file' }

    $seq1 = $m::ReadInt64($base, 24)
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 300
        $busy = $m::ReadInt32($base, 32)
        $seq2 = $m::ReadInt64($base, 24)
        if ($seq2 -ne $seq1 -and $busy -eq 0) { break }
    }
    Write-Output ("seq: first={0} then={1} busy={2} moved={3}" -f $seq1, $seq2, $busy, ($seq2 -ne $seq1))

    $pixels = [IntPtr]::Add($base, 64)
    $bmp = New-Object Drawing.Bitmap($w, $h, $stride, [Drawing.Imaging.PixelFormat]::Format32bppRgb, $pixels)
    $snap = New-Object Drawing.Bitmap($bmp)
    $snap.Save($Out, [Drawing.Imaging.ImageFormat]::Png)
    Write-Output ("saved snapshot: {0} ({1} bytes)" -f $Out, (Get-Item $Out).Length)
    $snap.Dispose()
    $bmp.Dispose()
}
finally {
    [void][ShmRead]::UnmapViewOfFile($base)
    [void][ShmRead]::CloseHandle($hMap)
    [void][ShmRead]::CloseHandle($hFile)
}
