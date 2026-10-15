# What kills the console session 2 minutes in? Plus the RDP state in this boot.
$ErrorActionPreference = 'Continue'

function Cut($t, $n) { if (-not $t) { return '' } $f = ($t -replace "`r?`n", ' '); if ($f.Length -gt $n) { return $f.Substring(0, $n) } return $f }

Write-Output '---- Winlogon/Operational (last 16) ----'
(Get-WinEvent -LogName 'Microsoft-Windows-Winlogon/Operational' -MaxEvents 16 -ErrorAction SilentlyContinue |
    ForEach-Object { '  ' + $_.TimeCreated.ToString('HH:mm:ss') + ' #' + $_.Id + ' ' + (Cut $_.Message 150) }) -join "`n"

Write-Output '---- System log (last 20) ----'
(Get-WinEvent -LogName System -MaxEvents 20 -ErrorAction SilentlyContinue |
    ForEach-Object { '  ' + $_.TimeCreated.ToString('HH:mm:ss') + ' #' + $_.Id + ' [' + $_.ProviderName + '] ' + (Cut $_.Message 120) }) -join "`n"

Write-Output '---- inactivity / logon policies ----'
foreach ($key in 'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System',
    'HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\LogonUI',
    'HKLM\SOFTWARE\Policies\Microsoft\Windows\System') {
    Write-Output ("  == $key")
    (& reg.exe query $key 2>&1 | Where-Object { $_ -match 'REG_' } | ForEach-Object { '    ' + $_.Trim() }) -join "`n"
}

Write-Output '---- RDS services ----'
foreach ($svc in 'TermService', 'UmRdpService', 'SessionEnv', 'CertPropSvc', 'RDPDR') {
    $out = (& sc.exe query $svc 2>&1 | Out-String)
    $state = ([regex]::Match($out, 'STATE\s+:\s+\d+\s+(\S+)')).Groups[1].Value
    $start = ([regex]::Match(((& sc.exe qc $svc 2>&1 | Out-String)), 'START_TYPE\s+:\s+\d+\s+(\S+)')).Groups[1].Value
    Write-Output ("  {0,-14} state={1,-12} start={2}" -f $svc, $state, $start)
}

Write-Output '---- terminal-server pipes ----'
((Get-ChildItem \\.\pipe\ -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'Term|RDP|Rd|ts' } | ForEach-Object { $_.Name }) -join ', ')

Write-Output '---- all pipes ----'
((Get-ChildItem \\.\pipe\ -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join ', ')
