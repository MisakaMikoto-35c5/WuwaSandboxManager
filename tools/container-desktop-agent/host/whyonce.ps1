$ErrorActionPreference = 'Continue'
$session = (Get-Process -Id $PID).SessionId
Remove-Item C:\wsm\borrowed-proof.txt, C:\wsm\once-redirect.log, C:\wsm\shot.png, C:\wsm\deskcap-once.log -Force -ErrorAction SilentlyContinue

Write-Output '---- borrowed: simple file write ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -Session $session -Desktop 'winsta0\default' -CommandLine 'C:\Windows\System32\cmd.exe /c echo borrowed-ok > C:\wsm\borrowed-proof.txt'
Start-Sleep -Seconds 4
Write-Output ('borrowed-proof.txt: ' + (Test-Path C:\wsm\borrowed-proof.txt))

Write-Output '---- borrowed: once capture, output redirected ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -Session $session -Desktop 'winsta0\default' -CommandLine 'C:\Windows\System32\cmd.exe /c "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\shot.png -Log C:\wsm\deskcap-once.log > C:\wsm\once-redirect.log 2>&1"'
Start-Sleep -Seconds 30
Write-Output ('once-redirect.log exists: ' + (Test-Path C:\wsm\once-redirect.log))
(Get-Content C:\wsm\once-redirect.log -Raw -ErrorAction SilentlyContinue)
Write-Output ('deskcap-once.log exists: ' + (Test-Path C:\wsm\deskcap-once.log))
(Get-Content C:\wsm\deskcap-once.log -Raw -ErrorAction SilentlyContinue)
Write-Output ('shot.png exists: ' + (Test-Path C:\wsm\shot.png))
