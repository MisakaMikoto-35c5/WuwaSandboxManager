# container-probe.ps1 - no elevation needed: q.ps1 sends commands to the alive runner.
$dd = "$env:TEMP\opencode\wsmdiag\dd"
$out = Join-Path $dd 'o.txt'

$lines = @(
    'cmd.exe /c echo ALIVE',
    'cmd.exe /c reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Userinit',
    'cmd.exe /c reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell',
    'cmd.exe /c reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v AutoAdminLogon',
    'cmd.exe /c reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" /v DefaultUserName',
    'cmd.exe /c reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager" /v NumberOfInitialSessions',
    'cmd.exe /c qwinsta',
    'cmd.exe /c tasklist'
)

foreach ($line in $lines) {
    Write-Output ("=== " + $line)
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dd 'q.ps1') -Line $line -TimeoutSec 200 2>&1 |
        Select-Object -Last 3 | Out-Null
    Start-Sleep -Milliseconds 400
}

Write-Output ''
Write-Output '===== raw runner output (last 120 lines) ====='
Get-Content $out -ErrorAction SilentlyContinue | Select-Object -Last 120
