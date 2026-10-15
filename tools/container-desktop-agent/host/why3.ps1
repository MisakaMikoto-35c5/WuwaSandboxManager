# (a) does the password we set at runtime still work in this boot?
# (b) what does winlogon/termsrv log?
$ErrorActionPreference = 'Continue'

Write-Output '---- run logontest.ps1 as SYSTEM ----'
Remove-Item C:\wsm\logontest.out -Force -ErrorAction SilentlyContinue
Set-Content -Path 'C:\wsm\logontest-run.cmd' -Encoding ASCII -Value @'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\logontest.ps1 > C:\wsm\logontest.out 2>&1
'@
& schtasks.exe /delete /tn wsm-lt /f 2>&1 | Out-Null
& schtasks.exe /create /tn wsm-lt /tr 'C:\wsm\logontest-run.cmd' /sc once /st 23:59 /ru SYSTEM /rl HIGHEST /f 2>&1 | Out-Null
& schtasks.exe /run /tn wsm-lt 2>&1 | Out-Null
for ($i = 0; $i -lt 120; $i++) {
    if ((Test-Path 'C:\wsm\logontest.out') -and (Get-Item 'C:\wsm\logontest.out').Length -gt 0) { Start-Sleep -Seconds 3; break }
    Start-Sleep -Milliseconds 500
}
(Get-Content 'C:\wsm\logontest.out' -Raw -ErrorAction SilentlyContinue)
& schtasks.exe /delete /tn wsm-lt /f 2>&1 | Out-Null

Write-Output '---- account state in this container ----'
(& net.exe user Administrator 2>&1 | Select-String 'Account active|Password last set|Password required|Last logon' | ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"

Write-Output '---- AutoLogon-related values ----'
(& reg.exe query 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' 2>&1 |
    Select-String 'AutoAdminLogon|AutoLogonCount|AutoLogonSID|DefaultUser|DefaultDomain|DefaultPass|ForceAutoLogon|^    Shell ' |
    ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"

Write-Output '---- Winlogon / LocalSessionManager logs ----'
foreach ($log in 'Microsoft-Windows-Winlogon/Operational',
    'Microsoft-Windows-TerminalServices-LocalSessionManager/Operational',
    'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational') {
    $events = Get-WinEvent -LogName $log -MaxEvents 10 -ErrorAction SilentlyContinue
    Write-Output ("  == $log : " + $(if ($events) { $events.Count } else { 0 }) + ' event(s)')
    if ($events) {
        ($events | ForEach-Object { '    ' + $_.TimeCreated.ToString('HH:mm:ss') + ' #' + $_.Id + ' ' + (($_.Message -replace "`r?`n", ' ')) }) -join "`n"
    }
}
