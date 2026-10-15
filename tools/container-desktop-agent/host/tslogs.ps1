$ErrorActionPreference = 'Continue'
function Show([string]$ln, [int]$n) {
    Write-Output ("--- " + $ln)
    try {
        Get-WinEvent -LogName $ln -MaxEvents $n -ErrorAction Stop |
            ForEach-Object { Write-Output ('  ' + $_.TimeCreated.ToString('HH:mm:ss') + ' #' + $_.Id + ' ' + (($_.Message -replace "`r?`n", ' ').Trim())) }
    }
    catch { Write-Output ('  ' + $_.Exception.Message) }
}

Write-Output '==== TS/RDP logs that exist ===='
Get-WinEvent -ListLog *TerminalServices* -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Output ('  ' + $_.LogName + '  rec=' + $_.RecordCount + ' enabled=' + $_.IsEnabled) }

Show 'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational' 6
Show 'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Admin' 8
Show 'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Debug' 12
Show 'Microsoft-Windows-TerminalServices-LocalSessionManager/Operational' 10

Write-Output '==== System: errors/warnings ===='
try {
    Get-WinEvent -FilterHashtable @{ LogName = 'System'; Level = 1, 2, 3 } -MaxEvents 30 -ErrorAction Stop |
        ForEach-Object { Write-Output ('  ' + $_.TimeCreated.ToString('HH:mm:ss') + ' ' + $_.ProviderName + ' #' + $_.Id + ' ' + (($_.Message -replace "`r?`n", ' ').Trim())) }
}
catch { Write-Output ('  ' + $_.Exception.Message) }

Write-Output '==== UmRdpService start attempt ===='
(& sc.exe start UmRdpService 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }
Start-Sleep -Seconds 3
(& sc.exe queryex UmRdpService 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }

Write-Output '==== RDP-Tcp WinStation ===='
(& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }

Write-Output '==== qwinsta /v ===='
(& qwinsta.exe /v 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }

Write-Output '==== WinStations subkeys ===='
(& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations' 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }
