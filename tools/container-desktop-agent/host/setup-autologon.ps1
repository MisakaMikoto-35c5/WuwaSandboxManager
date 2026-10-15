# Configure auto-logon + the console-session shell in the RUNNING container, then
# the next container start (same scratch layer) will perform a real logon.
$ErrorActionPreference = 'Continue'

$user = 'Administrator'
$password = 'Wsm!Passw0rd'

Write-Output '---- set the account password ----'
(& net.exe user $user $password 2>&1) -join "`n"

Write-Output '---- write the auto-logon + shell values ----'
$winlogon = 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
foreach ($pair in @(
        @('AutoAdminLogon', '1'),
        @('DefaultUserName', $user),
        @('DefaultDomainName', $env:COMPUTERNAME),
        @('DefaultPassword', $password),
        @('ForceAutoLogon', '1'),
        @('Shell', 'C:\wsm\agent.cmd'))) {
    (& reg.exe add $winlogon /v $pair[0] /t REG_SZ /d $pair[1] /f 2>&1) -join ' '
}
(& reg.exe query $winlogon 2>&1 |
    Select-String 'AutoAdminLogon|DefaultUser|DefaultDomain|DefaultPass|ForceAutoLogon|^    Shell' |
    ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"

Write-Output '---- C:\wsm ----'
(Get-ChildItem C:\wsm -ErrorAction SilentlyContinue | Select-Object Name, Length | Format-Table -AutoSize | Out-String)

Write-Output '---- this container is ready to be restarted ----'
