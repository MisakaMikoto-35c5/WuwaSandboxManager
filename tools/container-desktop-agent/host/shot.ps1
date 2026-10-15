# Put regedt32 on the container desktop, then take one screenshot to a file.
$ErrorActionPreference = 'Continue'
$session = (Get-Process -Id $PID).SessionId
Remove-Item C:\wsm\shot.png, C:\wsm\deskcap-once.log -Force -ErrorAction SilentlyContinue

Write-Output '---- regedt32 on the desktop (visible) ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\wsm\session-run.ps1' `
    -Session $session -Desktop 'winsta0\default' -Visible `
    -CommandLine 'C:\Windows\System32\regedt32.exe'

Write-Output '---- console on the desktop (visible) ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\wsm\session-run.ps1' `
    -Session $session -Desktop 'winsta0\default' -Visible `
    -CommandLine 'C:\Windows\System32\cmd.exe /k echo Windows container desktop'

Start-Sleep -Seconds 8

Write-Output '---- one-shot capture ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\wsm\session-run.ps1' `
    -Session $session -Desktop 'winsta0\default' `
    -CommandLine 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\shot.png -Log C:\wsm\deskcap-once.log'

Write-Output '---- result ----'
if (Test-Path C:\wsm\shot.png) { Write-Output ('shot.png: ' + (Get-Item C:\wsm\shot.png).Length + ' bytes') }
else { Write-Output 'shot.png: MISSING' }
(Get-Content 'C:\wsm\deskcap-once.log' -Raw -ErrorAction SilentlyContinue)
