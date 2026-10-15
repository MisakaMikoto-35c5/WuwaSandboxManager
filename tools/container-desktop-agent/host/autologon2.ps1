# Set up auto-logon at runtime and restart LogonUI so winlogon performs a real
# logon -> the console session gets a user, and Winlogon\Shell starts our agent.
$ErrorActionPreference = 'Continue'

$user = 'Administrator'
$password = 'Wsm!Passw0rd'

Write-Output '---- set the account password ----'
(& net.exe user $user $password 2>&1) -join "`n"

Write-Output '---- write the auto-logon values (container registry) ----'
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
    Select-String 'AutoAdminLogon|DefaultUser|DefaultDomain|DefaultPass|ForceAutoLogon|Shell' |
    ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"

Write-Output '---- agent in C:\wsm ----'
(Get-ChildItem C:\wsm -ErrorAction SilentlyContinue | Select-Object Name, Length | Format-Table -AutoSize | Out-String)

Write-Output '---- session 5 before ----'
((Get-Process | Where-Object { $_.SessionId -ne 0 -and $_.SessionId -ne (Get-Process -Id $PID).SessionId } |
        ForEach-Object { $_.ProcessName + '#' + $_.Id }) -join ', ')

Write-Output '---- restart LogonUI (winlogon re-reads auto-logon) ----'
(& taskkill.exe /f /im LogonUI.exe 2>&1) -join "`n"
Start-Sleep -Seconds 20

Write-Output '---- session 5 after ----'
((Get-Process | Where-Object { $_.SessionId -ne 0 -and $_.SessionId -ne (Get-Process -Id $PID).SessionId } |
        ForEach-Object { $_.ProcessName + '#' + $_.Id }) -join ', ')

Write-Output '---- query user / qwinsta ----'
(query user 2>&1 | Out-String)
(qwinsta 2>&1 | Out-String)

Write-Output '---- C:\wsm\deskcap-5901.log ----'
(Get-Content C:\wsm\deskcap-5901.log -Raw -ErrorAction SilentlyContinue)
