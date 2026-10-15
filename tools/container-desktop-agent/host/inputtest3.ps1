$ErrorActionPreference = 'Continue'
$sr = 'C:\wsm\session-run.ps1'
function Run([string] $cmd, [switch] $vis) {
    if ($vis) { return (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -Visible -CommandLine $cmd 2>&1 | Out-String) }
    return (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -CommandLine $cmd 2>&1 | Out-String)
}
Get-Process cmd -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item C:\wsm\verify.txt, C:\wsm\dig.log, C:\wsm\in-after.png -ErrorAction SilentlyContinue
Write-Output '=== start console (visible, stays open) ==='
Write-Output (Run 'cmd.exe /k title WSM-CONSOLE' -vis)
Start-Sleep -Seconds 6
Write-Output '=== drive input from a file (single child) ==='
Write-Output (Run 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskinput.ps1 -Action script -TextFile C:\wsm\steps.txt -Log C:\wsm\dig.log')
Start-Sleep -Seconds 4
Write-Output '=== capture ==='
Write-Output (Run 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\in-after.png')
Write-Output ('in-after.png = ' + (Get-Item C:\wsm\in-after.png -ErrorAction SilentlyContinue).Length + ' bytes')
Write-Output '=== dig.log ==='
(Get-Content C:\wsm\dig.log -ErrorAction SilentlyContinue) -join "`n"
Write-Output '=== verify.txt (proof the typed command reached the console) ==='
Write-Output ('exists=' + (Test-Path C:\wsm\verify.txt) + ' content=' + ((Get-Content C:\wsm\verify.txt -ErrorAction SilentlyContinue) -join ','))
