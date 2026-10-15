# Clear the auto-logon bookkeeping winlogon wrote after the first logon, so the
# next boot logs on again (and the shell runs in the console session).
$ErrorActionPreference = 'Continue'
$winlogon = 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'

(& reg.exe delete $winlogon /v AutoLogonSID /f 2>&1) -join ' '
(& reg.exe add $winlogon /v AutoAdminLogon /t REG_SZ /d 1 /f 2>&1) -join ' '
(& reg.exe add $winlogon /v DefaultUserName /t REG_SZ /d Administrator /f 2>&1) -join ' '
(& reg.exe add $winlogon /v DefaultPassword /t REG_SZ /d 'Wsm!Passw0rd' /f 2>&1) -join ' '
(& reg.exe add $winlogon /v ForceAutoLogon /t REG_SZ /d 1 /f 2>&1) -join ' '
(& reg.exe add $winlogon /v Shell /t REG_SZ /d 'C:\Windows\System32\cmd.exe /k C:\wsm\agent.cmd' /f 2>&1) -join ' '

Remove-Item C:\wsm\proof-shell.txt, C:\wsm\deskcap-5901.log -Force -ErrorAction SilentlyContinue

(& reg.exe query $winlogon 2>&1 |
    Select-String 'AutoAdminLogon|AutoLogonSID|DefaultUser|DefaultPass|ForceAutoLogon|^    Shell ' |
    ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"
(& net.exe user Administrator 2>&1 | Select-String 'Account active|Last logon' | ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"
