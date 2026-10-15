# mirror-e2e.ps1 - end-to-end test of the window mirror's DISPLAY + DRAG path, using the
# real production code on the host desktop (the read side normally runs inside the
# container's session; the code and the protocol are identical).
#
#   container window --(read side: read.log / mirror.shm)--> host mirror window
#   host drag --> movewindow command --> read side SetWindowPos --> the window MOVES
$ErrorActionPreference = 'Continue'
$root = "$env:TEMP\opencode\wsmdiag\mirror-test"
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $root -Force | Out-Null

$repo = 'C:\Users\User\Documents\WindowsSandboxManager'
Copy-Item "$repo\src\10-Hcs\Wsm.Hcs.Storage\Images\wsm-desktop-read.ps1" "$root\wsm-desktop-read.ps1" -Force
$wsm = "$repo\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe"
$mirror = "$root\mirror.shm"
$filter = 'wsm-mirror-demo'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WsmTestWin {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004;
}
'@

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$originalCursor = [System.Windows.Forms.Cursor]::Position
$results = New-Object System.Collections.Generic.List[string]

function Note([string] $m) { Write-Output $m; $results.Add($m) }

# ---- 1. a window to mirror -------------------------------------------------------
$targetFile = "$root\wsm-mirror-demo.txt"
Set-Content -Path $targetFile -Value 'window mirror drag test'
$notepad = Start-Process notepad.exe -ArgumentList $targetFile -PassThru
Start-Sleep -Seconds 3
$npHandle = (Get-Process -Id $notepad.Id).MainWindowHandle
[void][WsmTestWin]::SetWindowPos($npHandle, [IntPtr]::Zero, 120, 120, 520, 380, 0x0004)
Start-Sleep -Milliseconds 800
$before = New-Object WsmTestWin+RECT
[void][WsmTestWin]::GetWindowRect($npHandle, [ref]$before)
Note ("[1] target window pid=" + $notepad.Id + " hwnd=0x" + $npHandle.ToInt64().ToString('X') +
      " rect=(" + $before.L + "," + $before.T + ")")

# ---- 2. the read side (this is the container-side code) --------------------------
$readArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$root\wsm-desktop-read.ps1",
    '-AgentDirectory', $root, '-MirrorFile', 'mirror.shm', '-InputPrefix', 'input-',
    '-StopFile', 'agent-stop.txt', '-Width', $screen.Width, '-Height', $screen.Height,
    '-Fps', 4, '-Seconds', 180, '-TitleFilter', $filter)
$read = Start-Process powershell.exe -ArgumentList $readArgs -PassThru
for ($i = 0; $i -lt 40 -and -not (Test-Path $mirror); $i++) { Start-Sleep -Milliseconds 500 }
Note ("[2] read side pid=" + $read.Id + " published mirror.shm=" + (Test-Path $mirror) +
      " size=" + (Get-Item $mirror -ErrorAction SilentlyContinue).Length)

# ---- 3. the viewer: one host window per container window -------------------------
$view = Start-Process $wsm -PassThru -ArgumentList @('desktop-view', '--mode', 'windows',
    '--frame', $mirror, '--seconds', 90, '--input-prefix', 'input-')
Start-Sleep -Seconds 6
$viewLog = "$root\mirror-view.log"
$mirrorWindow = $null
if (Test-Path $viewLog) {
    $line = Select-String -Path $viewLog -Pattern '^.*window id=0x' | Select-Object -First 1
    Note ("[3] viewer pid=" + $view.Id + " log: " + ($line.Line -replace '^\S+\s+', ''))
    if ($line.Line -match 'rect=\((-?\d+),(-?\d+)') {
        $mirrorWindow = @{ X = [int]$Matches[1]; Y = [int]$Matches[2] }
    }
} else {
    Note '[3] viewer produced no log'
}
if (-not $mirrorWindow) { Note '[3] FAIL: no mirror window was created'; $mirrorWindow = @{ X = 0; Y = 0 } }

# ---- 4. drag the mirrored window's title band ------------------------------------
$grabX = $mirrorWindow.X + 60
$grabY = $mirrorWindow.Y + 12      # inside the 28px title band
[System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point($grabX, $grabY)
Start-Sleep -Milliseconds 400
[WsmTestWin]::mouse_event([WsmTestWin]::LEFTDOWN, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 300
foreach ($step in 1..5) {
    [System.Windows.Forms.Cursor]::Position = New-Object System.Drawing.Point(
        ($grabX + $step * 16), ($grabY + $step * 12))
    Start-Sleep -Milliseconds 180
}
[WsmTestWin]::mouse_event([WsmTestWin]::LEFTUP, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Seconds 3
[System.Windows.Forms.Cursor]::Position = $originalCursor
Note ("[4] dragged the mirror window from (" + $grabX + "," + $grabY + ") by (+80,+60)")

# ---- 5. did the real window move? ------------------------------------------------
$after = New-Object WsmTestWin+RECT
[void][WsmTestWin]::GetWindowRect($npHandle, [ref]$after)
$movedX = $after.L - $before.L
$movedY = $after.T - $before.T
Note ("[5] target window rect=(" + $after.L + "," + $after.T + ")  delta=(" + $movedX + "," + $movedY + ")")

$dragLog = @(Select-String -Path $viewLog -Pattern 'drag (begin|move|end)' -ErrorAction SilentlyContinue)
Note ("[6] viewer drag log lines: " + $dragLog.Count)
$dragLog | Select-Object -First 4 | ForEach-Object { Note ("      " + ($_.Line -replace '^\S+\s+', '')) }

$readLog = "$root\read.log"
$moveLines = @(Select-String -Path $readLog -Pattern 'movewindow' -ErrorAction SilentlyContinue)
Note ("[7] read side applied: " + ($(if ($moveLines.Count -gt 0) { ($moveLines[-1].Line -replace '^\S+\s+', '') } else { '(none)' })))

$verdict = ($moveLines.Count -gt 0) -and ([Math]::Abs($movedX) -ge 50) -and ([Math]::Abs($movedY) -ge 35)
Note ("[=] VERDICT display=" + $(if ($mirrorWindow.X -ne 0) { 'ok' } else { 'fail' }) +
      " drag=" + $(if ($verdict) { 'ok' } else { 'fail' }))

# ---- cleanup ---------------------------------------------------------------------
[WsmTestWin]::SetWindowPos($npHandle, [IntPtr]::Zero, 120, 120, 520, 380, 0x0004)
Stop-Process -Id $view.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $read.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $notepad.Id -Force -ErrorAction SilentlyContinue
Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like '*wsm-mirror-demo*' } | Stop-Process -Force -ErrorAction SilentlyContinue
$results | Set-Content "$root\result.txt"
