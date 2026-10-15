# deskview.ps1 (HOST side) - show the container's desktop in a host window, fed by
# the shared frame file, and forward local mouse/keyboard back to the container.
#
#   powershell -File deskview.ps1 -Seconds 60          # a window, auto-closed
#   powershell -File deskview.ps1 -Capture out.png     # headless: one frame to PNG
#
# No TCP: the frame buffer is the file deskshare.ps1 maps inside the container
# (see its header for the layout), and input goes back as one small file per
# command in the image layer.
param(
    [string] $Mount,
    [string] $Rel = 'wsm\frame.shm',
    [string] $InboxDir = 'C:\wsm-images\servercore-ltsc2022\Files\wsm',
    [string] $Capture = '',
    [int] $Seconds = 0,
    [int] $WaitSeconds = 60
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShmView {
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

$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline) {
    if ((Test-Path $path) -and ((Get-Item $path).Length -gt 64)) { break }
    Start-Sleep -Milliseconds 500
}
if (-not (Test-Path $path)) { throw "frame file never appeared: $path" }

$GENERIC_READ = [uint32]2147483648  # 0x80000000
$OPEN_EXISTING = [uint32]3
$PAGE_READONLY = [uint32]0x02
$FILE_MAP_READ = [uint32]0x04
$SHARE_ALL = [uint32]7
$ATTR_NORMAL = [uint32]0x80

$hFile = [ShmView]::CreateFileW($path, $GENERIC_READ, $SHARE_ALL, [IntPtr]::Zero, $OPEN_EXISTING, $ATTR_NORMAL, [IntPtr]::Zero)
if ($hFile -eq [IntPtr](-1)) { throw ('CreateFileW failed: ' + $m::GetLastWin32Error()) }
$hMap = [ShmView]::CreateFileMappingW($hFile, [IntPtr]::Zero, $PAGE_READONLY, 0, 0, $null)
if ($hMap -eq [IntPtr]::Zero) { throw ('CreateFileMappingW failed: ' + $m::GetLastWin32Error()) }
$base = [ShmView]::MapViewOfFile($hMap, $FILE_MAP_READ, 0, 0, [uint64]0)
if ($base -eq [IntPtr]::Zero) { throw ('MapViewOfFile failed: ' + $m::GetLastWin32Error()) }

$w = $m::ReadInt32($base, 8)
$h = $m::ReadInt32($base, 12)
$stride = $m::ReadInt32($base, 16)
Write-Output ("viewing {0}  {1}x{2} stride={3} writerPid={4}" -f $path, $w, $h, $stride, $m::ReadInt32($base, 36))

$pixels = [IntPtr]::Add($base, 64)
$frame = New-Object Drawing.Bitmap($w, $h, $stride, [Drawing.Imaging.PixelFormat]::Format32bppRgb, $pixels)

function Send-Container([string] $cmd) {
    try {
        $name = 'inbox-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff') + '.txt'
        [IO.File]::WriteAllText((Join-Path $InboxDir $name), $cmd, [Text.Encoding]::ASCII)
    }
    catch { }
}

if ($Capture) {
    $snap = New-Object Drawing.Bitmap($frame)
    $snap.Save($Capture, [Drawing.Imaging.ImageFormat]::Png)
    Write-Output ("saved {0} ({1} bytes)" -f $Capture, (Get-Item $Capture).Length)
    $snap.Dispose()
    $frame.Dispose(); [void][ShmView]::UnmapViewOfFile($base)
    [void][ShmView]::CloseHandle($hMap); [void][ShmView]::CloseHandle($hFile)
    return
}

$script:lastSeq = [int64]-1
$script:lastMove = Get-Date

$form = New-Object Windows.Forms.Form
$form.Text = 'WSM - container desktop over shared memory'
$form.ClientSize = New-Object Drawing.Size($w, $h)
$form.StartPosition = 'CenterScreen'
$pic = New-Object Windows.Forms.PictureBox
$pic.Size = New-Object Drawing.Size($w, $h)
$pic.Location = New-Object Drawing.Point(0, 0)
$pic.SizeMode = [Windows.Forms.PictureBoxSizeMode]::Normal
$form.Controls.Add($pic)

$form.Add_MouseDown({
        param($s, $e)
        $b = 1
        if ($e.Button -eq [Windows.Forms.MouseButtons]::Right) { $b = 2 }
        elseif ($e.Button -eq [Windows.Forms.MouseButtons]::Middle) { $b = 3 }
        Send-Container ("click {0},{1},{2}" -f $e.X, $e.Y, $b)
    })
$form.Add_MouseMove({
        param($s, $e)
        if (((Get-Date) - $script:lastMove).TotalMilliseconds -lt 80) { return }
        $script:lastMove = Get-Date
        Send-Container ("move {0},{1}" -f $e.X, $e.Y)
    })
$form.Add_KeyDown({
        param($s, $e)
        $k = $e.KeyCode.ToString().ToUpperInvariant()
        switch ($k) {
            'ENTER' { Send-Container 'keys ENTER' }
            'TAB' { Send-Container 'keys TAB' }
            'ESCAPE' { Send-Container 'keys ESC' }
            'BACK' { Send-Container 'keys BACKSPACE' }
            'DELETE' { Send-Container 'keys DELETE' }
            'UP' { Send-Container 'keys UP' }
            'DOWN' { Send-Container 'keys DOWN' }
            'LEFT' { Send-Container 'keys LEFT' }
            'RIGHT' { Send-Container 'keys RIGHT' }
            default {
                if ($k -match '^F([1-9]|1[0-9]|2[0-4])$') { Send-Container ("keys {0}" -f $k) }
                elseif ($k.Length -eq 1 -and $k -match '^[A-Z0-9]$') { Send-Container ('text ' + $k.ToLowerInvariant()) }
            }
        }
    })
$form.KeyPreview = $true

$script:seq = [int64]0
$script:gotFrame = $false
$timer = New-Object Windows.Forms.Timer
$timer.Interval = 100
$timer.Add_Tick({
        if ($m::ReadInt32($base, 32) -ne 0) { return }
        $s = $m::ReadInt64($base, 24)
        if ($s -eq $script:lastSeq) { return }
        $script:lastSeq = $s
        $snap = New-Object Drawing.Bitmap($frame)
        $old = $pic.Image
        $pic.Image = $snap
        if ($old) { $old.Dispose() }
        $script:gotFrame = $true
        $script:seq = $s
        $form.Text = ("WSM - container desktop over shared memory   frame {0}" -f $s)
    })
$form.Add_Shown({ $timer.Start(); $form.Activate() })

if ($Seconds -gt 0) {
    $closeTimer = New-Object Windows.Forms.Timer
    $closeTimer.Interval = $Seconds * 1000
    $closeTimer.Add_Tick({ $closeTimer.Stop(); $form.Close() })
    $form.Add_Shown({ $closeTimer.Start() })
}

[void]$form.ShowDialog()
$timer.Stop()
Write-Output ("closed; lastFrame={0} gotFrame={1}" -f $script:seq, $script:gotFrame)
$frame.Dispose(); [void][ShmView]::UnmapViewOfFile($base)
[void][ShmView]::CloseHandle($hMap); [void][ShmView]::CloseHandle($hFile)
