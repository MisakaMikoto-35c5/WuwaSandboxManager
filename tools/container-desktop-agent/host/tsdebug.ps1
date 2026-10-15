$ErrorActionPreference = 'Continue'
$logs = 'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Debug',
        'Microsoft-Windows-TerminalServices-LocalSessionManager/Debug',
        'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational'

Write-Output '==== enable analytic/debug logs ===='
foreach ($l in $logs) { Write-Output ('  ' + ((& wevtutil.exe sl $l /e:true 2>&1) -join ' ')) }

Write-Output '==== clear RCM operational (to see a clean restart) ===='
(& wevtutil.exe cl 'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational' 2>&1) | Out-Null
(& wevtutil.exe cl 'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Debug' 2>&1) | Out-Null
(& wevtutil.exe cl 'Microsoft-Windows-TerminalServices-LocalSessionManager/Debug' 2>&1) | Out-Null

Write-Output '==== restart TermService ===='
(& sc.exe stop TermService 2>&1) | Out-Null
Start-Sleep -Seconds 6
(& sc.exe start TermService 2>&1) | Out-Null
Start-Sleep -Seconds 10
(& sc.exe query TermService 2>&1) | Select-String 'STATE' | ForEach-Object { Write-Output ('  ' + $_) }

Write-Output '==== RCM/Debug (oldest first) ===='
try {
    Get-WinEvent -LogName 'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Debug' -Oldest -MaxEvents 120 -ErrorAction Stop |
        ForEach-Object { Write-Output ('  ' + $_.TimeCreated.ToString('HH:mm:ss.fff') + ' #' + $_.Id + ' ' + (($_.Message -replace "`r?`n", ' ').Trim())) }
}
catch { Write-Output ('  ' + $_.Exception.Message) }

Write-Output '==== RCM/Operational after restart ===='
Get-WinEvent -LogName 'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational' -MaxEvents 20 -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Output ('  ' + $_.TimeCreated.ToString('HH:mm:ss') + ' #' + $_.Id + ' ' + (($_.Message -replace "`r?`n", ' ').Trim())) }

Write-Output '==== run as SYSTEM? check TermService account ===='
(& sc.exe qc TermService 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }
