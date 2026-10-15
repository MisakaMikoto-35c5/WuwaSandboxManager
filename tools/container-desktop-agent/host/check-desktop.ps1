# After the restart: did winlogon auto-logon, and is the console-session agent up?
$ErrorActionPreference = 'Continue'

Write-Output '---- Winlogon values seen by this container ----'
(& reg.exe query 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' 2>&1 |
    Select-String 'AutoAdminLogon|DefaultUser|DefaultDomain|DefaultPass|ForceAutoLogon|^    Shell' |
    ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"

Write-Output '---- C:\wsm ----'
(Get-ChildItem C:\wsm -ErrorAction SilentlyContinue | Select-Object Name, Length | Format-Table -AutoSize | Out-String)

Write-Output '---- sessions ----'
(qwinsta 2>&1 | Out-String)

Write-Output '---- processes per session ----'
(Get-Process | Group-Object SessionId | ForEach-Object {
        "  session {0}: {1} procs: {2}" -f $_.Name, $_.Count,
        (($_.Group | Select-Object -ExpandProperty ProcessName | Sort-Object -Unique) -join ',')
    }) -join "`n"

Write-Output '---- query user ----'
(query user 2>&1 | Out-String)

Write-Output '---- deskcap log ----'
(Get-Content C:\wsm\deskcap-5901.log -Raw -ErrorAction SilentlyContinue)
