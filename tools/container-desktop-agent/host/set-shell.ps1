# Point Winlogon\Shell at a real executable command line (the agent script).
$ErrorActionPreference = 'Continue'
$winlogon = 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$shell = 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\agent.ps1 -Port 5901'

(& reg.exe add $winlogon /v Shell /t REG_SZ /d $shell /f 2>&1) -join ' '
(& reg.exe add $winlogon /v AutoAdminLogon /t REG_SZ /d 1 /f 2>&1) -join ' '
(& reg.exe query $winlogon /v Shell 2>&1 | ForEach-Object { '  ' + $_.Trim() }) -join "`n"
(& reg.exe query 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' 2>&1 |
    Select-String 'AutoAdminLogon|DefaultUser|DefaultDomain|DefaultPass|ForceAutoLogon|^    Shell ' |
    ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"
