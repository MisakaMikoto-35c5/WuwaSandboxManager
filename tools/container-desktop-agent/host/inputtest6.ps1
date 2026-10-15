$ErrorActionPreference = 'Continue'
$sr = 'C:\wsm\session-run.ps1'
Remove-Item C:\wsm\i3.log, C:\wsm\find.png -ErrorAction SilentlyContinue
Write-Output '=== input sequence: info / keys F3 / info (Registry Editor Find dialog) ==='
$o = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -CommandLine 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskinput.ps1 -Action script -TextFile C:\wsm\steps.txt -Log C:\wsm\i3.log' 2>&1
Write-Output (($o | Out-String).Trim())
Start-Sleep -Seconds 3
$o2 = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -Visible -CommandLine 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\find.png' 2>&1
Write-Output (($o2 | Out-String).Trim())
Write-Output ('find.png = ' + (Get-Item C:\wsm\find.png -ErrorAction SilentlyContinue).Length + ' bytes')
Write-Output '=== i3.log ==='
(Get-Content C:\wsm\i3.log -ErrorAction SilentlyContinue) -join "`n"
