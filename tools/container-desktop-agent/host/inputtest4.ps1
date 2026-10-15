$ErrorActionPreference = 'Continue'
$sr = 'C:\wsm\session-run.ps1'
function Run([string] $cmd, [switch] $vis) {
    if ($vis) { return (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -Visible -CommandLine $cmd 2>&1 | Out-String) }
    return (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -CommandLine $cmd 2>&1 | Out-String)
}
Get-Process cmd, powershell -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq 4 } | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item C:\wsm\err.png, C:\wsm\dig2.log -ErrorAction SilentlyContinue
Write-Output '=== run deskinput VISIBLY so its console shows the error ==='
Write-Output (Run 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskinput.ps1 -Action script -TextFile C:\wsm\steps.txt -Log C:\wsm\dig2.log' -vis)
Start-Sleep -Seconds 8
Write-Output '=== capture the desktop VISIBLY ==='
Write-Output (Run 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\err.png' -vis)
Start-Sleep -Seconds 3
Write-Output ('err.png = ' + (Get-Item C:\wsm\err.png -ErrorAction SilentlyContinue).Length + ' bytes')
Write-Output ('dig2.log = ' + ((Get-Content C:\wsm\dig2.log -ErrorAction SilentlyContinue) -join ' | '))
