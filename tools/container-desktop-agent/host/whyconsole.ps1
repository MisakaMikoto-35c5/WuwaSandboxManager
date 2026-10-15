# Why is there no console session now?
$ErrorActionPreference = 'Continue'

Write-Output '---- sessions ----'
(qwinsta 2>&1 | Out-String)
(query session 2>&1 | Out-String)

Write-Output '---- NumberOfInitialSessions seen by the container ----'
(& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Control\Session Manager' /v NumberOfInitialSessions 2>&1 | ForEach-Object { '  ' + $_.Trim() }) -join "`n"

Write-Output '---- all processes (name#pid@session) ----'
((Get-Process | Sort-Object SessionId | ForEach-Object { $_.ProcessName + '#' + $_.Id + '@' + $_.SessionId }) -join ', ')

Write-Output '---- System log (last 25) ----'
(Get-WinEvent -LogName System -MaxEvents 25 -ErrorAction SilentlyContinue |
    ForEach-Object { '  {0} #{1} {2}' -f $_.TimeCreated.ToString('HH:mm:ss'), $_.Id, (($_.Message -replace "`r?`n", ' ').Substring(0, [Math]::Min(160, $_.Message.Length))) }) -join "`n"

Write-Output '---- Winlogon / TerminalServices operational logs ----'
foreach ($log in 'Microsoft-Windows-Winlogon/Operational',
    'Microsoft-Windows-TerminalServices-LocalSessionManager/Operational',
    'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational',
    'Microsoft-Windows-User Profiles Service/Operational') {
    $events = Get-WinEvent -LogName $log -MaxEvents 8 -ErrorAction SilentlyContinue
    if ($events) {
        Write-Output ("  == $log")
        ($events | ForEach-Object { '    {0} #{1} {2}' -f $_.TimeCreated.ToString('HH:mm:ss'), $_.Id, (($_.Message -replace "`r?`n", ' ').Substring(0, [Math]::Min(140, $_.Message.Length))) }) -join "`n"
    }
    else { Write-Output ("  == $log : (no events)") }
}

Write-Output '---- Security log: logon/logoff ----'
(Get-WinEvent -LogName Security -MaxEvents 400 -ErrorAction SilentlyContinue |
    Where-Object { $_.Id -in 4624, 4625, 4634, 4647, 4648, 4672 } |
    Select-Object -First 12 |
    ForEach-Object { '  {0} #{1} {2}' -f $_.TimeCreated.ToString('HH:mm:ss'), $_.Id, (($_.Message -replace "`r?`n", ' ').Substring(0, [Math]::Min(120, $_.Message.Length))) }) -join "`n"

Write-Output '---- C:\wsm contents ----'
(Get-ChildItem C:\wsm -ErrorAction SilentlyContinue | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize | Out-String)
