# Launch the capture agent + regedt32 on the container's own desktop (the session
# the container's user runs in) and let the host grab a screenshot.
$ErrorActionPreference = 'Continue'

$session = (Get-Process -Id $PID).SessionId
Write-Output ("container admin process runs in session " + $session)

Write-Output '---- launch deskcap ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\wsm\session-run.ps1' `
    -Session $session -Desktop 'winsta0\default' `
    -CommandLine 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Port 5901 -Log C:\wsm\deskcap-5901.log'

Write-Output '---- launch regedt32 (visible) ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\wsm\session-run.ps1' `
    -Session $session -Desktop 'winsta0\default' -Visible `
    -CommandLine 'C:\Windows\System32\regedt32.exe'

Write-Output '---- launch a console (visible) ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\wsm\session-run.ps1' `
    -Session $session -Desktop 'winsta0\default' -Visible `
    -CommandLine 'C:\Windows\System32\cmd.exe /k echo Windows container desktop %USERNAME% @ %COMPUTERNAME%'

Start-Sleep -Seconds 20
Write-Output '---- deskcap log ----'
(Get-Content 'C:\wsm\deskcap-5901.log' -Raw -ErrorAction SilentlyContinue)
Write-Output '---- processes per session ----'
(Get-Process | Group-Object SessionId | ForEach-Object {
        "  session {0}: {1} procs: {2}" -f $_.Name, $_.Count,
        (($_.Group | Select-Object -ExpandProperty ProcessName | Sort-Object -Unique) -join ',')
    }) -join "`n"
