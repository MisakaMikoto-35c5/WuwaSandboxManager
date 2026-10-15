$ErrorActionPreference = 'Continue'
$session = (Get-Process -Id $PID).SessionId
Set-Content -Path C:\wsm\once.cmd -Encoding ASCII -Value @"
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\shot.png -Log C:\wsm\deskcap-once.log > C:\wsm\once-redirect.log 2>&1
"@
Remove-Item C:\wsm\shot.png, C:\wsm\once-redirect.log, C:\wsm\deskcap-once.log, C:\wsm\shot-direct.png -Force -ErrorAction SilentlyContinue

Write-Output '---- regedt32 + console on the active desktop (Winlogon) ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -Session $session -Desktop 'winsta0\Winlogon' -Visible -CommandLine 'C:\Windows\System32\regedt32.exe'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -Session $session -Desktop 'winsta0\Winlogon' -Visible -CommandLine 'C:\Windows\System32\cmd.exe /k echo container desktop on the active desktop'
Start-Sleep -Seconds 8

Write-Output '---- capture the active desktop ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -Session $session -Desktop 'winsta0\Winlogon' -CommandLine 'C:\Windows\System32\cmd.exe /c C:\wsm\once.cmd'
Start-Sleep -Seconds 30
foreach ($f in 'once-redirect.log', 'shot.png') {
    $p = "C:\wsm\$f"
    Write-Output ("{0}: exists={1} size={2}" -f $f, (Test-Path $p), (Get-Item $p -ErrorAction SilentlyContinue).Length)
}
(Get-Content C:\wsm\once-redirect.log -Raw -ErrorAction SilentlyContinue)

Write-Output '---- direct run (service window station) ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\shot-direct.png -Log C:\wsm\dc-direct.log 2>&1 | Out-String
Write-Output ('shot-direct.png: exists=' + (Test-Path C:\wsm\shot-direct.png) + ' size=' + (Get-Item C:\wsm\shot-direct.png -ErrorAction SilentlyContinue).Length)
(Get-Content C:\wsm\dc-direct.log -Raw -ErrorAction SilentlyContinue)
