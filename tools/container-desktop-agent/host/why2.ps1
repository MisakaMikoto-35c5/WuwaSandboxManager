# Lean "why is there no console session" probe.
$ErrorActionPreference = 'Continue'

function Cut($text, $max) {
    if (-not $text) { return '' }
    $flat = ($text -replace "`r?`n", ' ')
    if ($flat.Length -gt $max) { return $flat.Substring(0, $max) }
    return $flat
}

Write-Output '---- qwinsta ----'
(qwinsta 2>&1 | Out-String)

Write-Output '---- NumberOfInitialSessions (in this container) ----'
(& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Control\Session Manager' /v NumberOfInitialSessions 2>&1) -join ' '

Write-Output '---- processes ----'
((Get-Process | Sort-Object SessionId | ForEach-Object { $_.ProcessName + '@' + $_.SessionId }) -join ', ')

Write-Output '---- System log (last 12) ----'
(Get-WinEvent -LogName System -MaxEvents 12 -ErrorAction SilentlyContinue |
    ForEach-Object { '  ' + $_.TimeCreated.ToString('HH:mm:ss') + ' #' + $_.Id + ' ' + (Cut $_.Message 130) }) -join "`n"

Write-Output '---- Winlogon/Operational (last 8) ----'
(Get-WinEvent -LogName 'Microsoft-Windows-Winlogon/Operational' -MaxEvents 8 -ErrorAction SilentlyContinue |
    ForEach-Object { '  ' + $_.TimeCreated.ToString('HH:mm:ss') + ' #' + $_.Id + ' ' + (Cut $_.Message 130) }) -join "`n"

Write-Output '---- deskcap log ----'
(Get-Content 'C:\wsm\deskcap-5901.log' -Raw -ErrorAction SilentlyContinue)

Write-Output '---- C:\wsm ----'
(Get-ChildItem C:\wsm -ErrorAction SilentlyContinue | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize | Out-String)
