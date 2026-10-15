$ErrorActionPreference = 'Continue'
$sr = 'C:\wsm\session-run.ps1'
function Run([string] $cmd, [switch] $vis) {
    if ($vis) { return (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -Visible -CommandLine $cmd 2>&1 | Out-String) }
    return (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -CommandLine $cmd 2>&1 | Out-String)
}
Remove-Item C:\wsm\i1.log, C:\wsm\i2.log, C:\wsm\input.png -ErrorAction SilentlyContinue
Write-Output '=== 1. launch notepad (visible) ==='
Write-Output (Run 'notepad.exe' -vis)
Start-Sleep -Seconds 8
Write-Output '=== 2. info BEFORE input ==='
Write-Output (Run 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskinput.ps1 -Action script -TextFile C:\wsm\steps.txt -Log C:\wsm\i2.log')
Start-Sleep -Seconds 3
Write-Output '=== 3. capture ==='
Write-Output (Run 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\input.png' -vis)
Start-Sleep -Seconds 2
Write-Output ('input.png = ' + (Get-Item C:\wsm\input.png -ErrorAction SilentlyContinue).Length + ' bytes')
Write-Output '=== 4. deskinput log (shows the foreground window before/after each step) ==='
(Get-Content C:\wsm\i2.log -ErrorAction SilentlyContinue) -join "`n"
