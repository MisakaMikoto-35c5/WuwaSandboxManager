# The session's logon path complains "Remote Desktop Service start failed
# 0x80070422" (ERROR_SERVICE_DISABLED). Enable TermService and look for profile
# errors from the previous boot.
$ErrorActionPreference = 'Continue'

Write-Output '---- Application log: profile / logon errors ----'
(Get-WinEvent -LogName Application -MaxEvents 200 -ErrorAction SilentlyContinue |
    Where-Object { $_.ProviderName -match 'User Profile|Winlogon|Group Policy|Windows Logon|Profiles' -or $_.Message -match 'profile|logon' } |
    Select-Object -First 15 |
    ForEach-Object { '  ' + $_.TimeCreated.ToString('HH:mm:ss') + ' [' + $_.ProviderName + '] #' + $_.Id + ' ' + (($_.Message -replace "`r?`n", ' ')) }) -join "`n"

Write-Output '---- User Profile Service log ----'
(Get-WinEvent -LogName 'Microsoft-Windows-User Profile Service/Operational' -MaxEvents 10 -ErrorAction SilentlyContinue |
    ForEach-Object { '  ' + $_.TimeCreated.ToString('HH:mm:ss') + ' #' + $_.Id + ' ' + (($_.Message -replace "`r?`n", ' ')) }) -join "`n"

Write-Output '---- enable TermService ----'
(& sc.exe config TermService start= auto 2>&1) -join "`n"
(& sc.exe start TermService 2>&1) -join "`n"
(& sc.exe query TermService 2>&1 | Select-String 'STATE|START_TYPE' | ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"
(& sc.exe qc TermService 2>&1 | Select-String 'START_TYPE' | ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"

Write-Output '---- also let UmRdpService try ----'
(& sc.exe config UmRdpService start= demand 2>&1) -join "`n"

Write-Output '---- clear the proof markers ----'
Remove-Item C:\wsm\proof-shell.txt, C:\wsm\deskcap-5901.log -Force -ErrorAction SilentlyContinue
