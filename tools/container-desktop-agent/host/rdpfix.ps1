$ErrorActionPreference = 'Continue'
$ts = 'HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server'
$rdp = "$ts\WinStations\RDP-Tcp"
$cv = 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion'

Write-Output '---- apply the RDP knobs (StartRCM was 0) ----'
foreach ($x in @(
        @($ts, 'StartRCM', 'REG_DWORD', '1'),
        @($ts, 'fDenyTSConnections', 'REG_DWORD', '0'),
        @($ts, 'AllowListenersInsideArgonContainer', 'REG_DWORD', '1'),
        @($rdp, 'AllowListenersInsideArgonContainer', 'REG_DWORD', '1'),
        @($rdp, 'SecurityLayer', 'REG_DWORD', '0'),
        @($rdp, 'UserAuthentication', 'REG_DWORD', '0'),
        @($rdp, 'fEnableWinStation', 'REG_DWORD', '1'),
        @($cv, 'AllowListenersInsideArgonContainer', 'REG_DWORD', '1'),
        @($cv, 'EditionID', 'REG_SZ', 'ContainerOSPlus'))) {
    (& reg.exe add $x[0] /v $x[1] /t $x[2] /d $x[3] /f 2>&1) -join ' '
}

Write-Output '---- services ----'
(& sc.exe config TermService start= auto 2>&1) -join ' '
(& sc.exe config UmRdpService start= demand 2>&1) -join ' '
(& sc.exe start UmRdpService 2>&1) -join ' '
(& sc.exe query UmRdpService 2>&1 | Select-String 'STATE') -join ' '

Write-Output '---- read back ----'
(& reg.exe query $ts 2>&1 | Select-String 'StartRCM|fDenyTS|AllowListeners' | ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"
(& reg.exe query $rdp 2>&1 | Select-String 'AllowListeners|SecurityLayer|UserAuthentication|fEnableWinStation|PortNumber' | ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"
(& reg.exe query $cv 2>&1 | Select-String 'AllowListeners|EditionID' | ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"

Write-Output '---- RCM log (before restart) ----'
(Get-WinEvent -LogName 'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational' -MaxEvents 6 -ErrorAction SilentlyContinue |
    ForEach-Object { '  ' + $_.TimeCreated.ToString('HH:mm:ss') + ' #' + $_.Id + ' ' + (($_.Message -replace "`r?`n", ' ')) }) -join "`n"
