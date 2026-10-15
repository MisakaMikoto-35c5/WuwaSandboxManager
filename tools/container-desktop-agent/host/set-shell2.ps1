# Point Winlogon\Shell at a real executable command line that keeps the session alive.
$ErrorActionPreference = 'Continue'
$winlogon = 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$shell = 'C:\Windows\System32\cmd.exe /k C:\wsm\agent.cmd'

(& reg.exe add $winlogon /v Shell /t REG_SZ /d $shell /f 2>&1) -join ' '
(& reg.exe add $winlogon /v AutoAdminLogon /t REG_SZ /d 1 /f 2>&1) -join ' '
(& reg.exe add $winlogon /v ForceAutoLogon /t REG_SZ /d 1 /f 2>&1) -join ' '
Remove-Item C:\wsm\proof-shell.txt -Force -ErrorAction SilentlyContinue
(& reg.exe query $winlogon 2>&1 |
    Select-String 'AutoAdminLogon|DefaultUser|DefaultDomain|DefaultPass|ForceAutoLogon|^    Shell ' |
    ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"
