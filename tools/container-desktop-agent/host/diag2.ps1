$ErrorActionPreference = 'Continue'
$sr = 'C:\wsm\session-run.ps1'

Write-Output '=== A. hidden cmd writes a proof file ==='
Remove-Item C:\wsm\proof.txt -ErrorAction SilentlyContinue
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -CommandLine 'cmd.exe /c echo hello > C:\wsm\proof.txt'
Start-Sleep -Seconds 4
Write-Output ('  proof.txt exists=' + (Test-Path C:\wsm\proof.txt) + ' content=' + ((Get-Content C:\wsm\proof.txt -ErrorAction SilentlyContinue) -join ','))

Write-Output '=== B. hidden powershell -Command writes a proof file ==='
Remove-Item C:\wsm\proof2.txt -ErrorAction SilentlyContinue
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -CommandLine 'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "''hi'' | Set-Content C:\wsm\proof2.txt"'
Start-Sleep -Seconds 8
Write-Output ('  proof2.txt exists=' + (Test-Path C:\wsm\proof2.txt) + ' content=' + ((Get-Content C:\wsm\proof2.txt -ErrorAction SilentlyContinue) -join ','))

Write-Output '=== C. visible deskcap -Once ==='
Remove-Item C:\wsm\b2.png, C:\wsm\d3.log -ErrorAction SilentlyContinue
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -Visible -CommandLine 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\b2.png -Log C:\wsm\d3.log'
Start-Sleep -Seconds 15
Write-Output ('  b2.png exists=' + (Test-Path C:\wsm\b2.png) + ' size=' + (Get-Item C:\wsm\b2.png -ErrorAction SilentlyContinue).Length)
Write-Output ('  d3.log: ' + ((Get-Content C:\wsm\d3.log -ErrorAction SilentlyContinue) -join ' | '))

Write-Output '=== D. hidden deskcap -Once ==='
Remove-Item C:\wsm\b3.png, C:\wsm\d4.log -ErrorAction SilentlyContinue
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -CommandLine 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\b3.png -Log C:\wsm\d4.log'
Start-Sleep -Seconds 15
Write-Output ('  b3.png exists=' + (Test-Path C:\wsm\b3.png) + ' size=' + (Get-Item C:\wsm\b3.png -ErrorAction SilentlyContinue).Length)
Write-Output ('  d4.log: ' + ((Get-Content C:\wsm\d4.log -ErrorAction SilentlyContinue) -join ' | '))

Write-Output '=== E. is regedt32 running? ==='
(& tasklist.exe /fi "imagename eq regedt32.exe" 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }
Write-Output '=== F. C:\wsm listing ==='
Get-ChildItem C:\wsm -File -ErrorAction SilentlyContinue | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize | Out-String | Write-Output
