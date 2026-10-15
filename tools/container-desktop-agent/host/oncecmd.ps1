$ErrorActionPreference = 'Continue'
$session = (Get-Process -Id $PID).SessionId

Set-Content -Path C:\wsm\once.cmd -Encoding ASCII -Value @"
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\shot.png -Log C:\wsm\deskcap-once.log > C:\wsm\once-redirect.log 2>&1
"@
Remove-Item C:\wsm\shot.png, C:\wsm\deskcap-once.log, C:\wsm\once-redirect.log -Force -ErrorAction SilentlyContinue

Write-Output '---- regedt32 + console on the desktop ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -Session $session -Desktop 'winsta0\default' -Visible -CommandLine 'C:\Windows\System32\regedt32.exe'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -Session $session -Desktop 'winsta0\default' -Visible -CommandLine 'C:\Windows\System32\cmd.exe /k echo Windows container desktop'
Start-Sleep -Seconds 8

Write-Output '---- borrowed one-shot capture via cmd file ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -Session $session -Desktop 'winsta0\default' -CommandLine 'C:\Windows\System32\cmd.exe /c C:\wsm\once.cmd'
Start-Sleep -Seconds 30

foreach ($f in 'once-redirect.log', 'deskcap-once.log', 'shot.png') {
    $p = "C:\wsm\$f"
    Write-Output ("{0}: exists={1} size={2}" -f $f, (Test-Path $p), (Get-Item $p -ErrorAction SilentlyContinue).Length)
}
Write-Output '---- redirect log ----'
(Get-Content C:\wsm\once-redirect.log -Raw -ErrorAction SilentlyContinue)
Write-Output '---- deskcap log ----'
(Get-Content C:\wsm\deskcap-once.log -Raw -ErrorAction SilentlyContinue)
