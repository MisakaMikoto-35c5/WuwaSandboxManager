$ErrorActionPreference = 'Continue'
$ts = 'HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server'
$rdp = "$ts\WinStations\RDP-Tcp"
$cv = 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion'

function Rq([string]$k, [string]$v) {
    $o = (& reg.exe query $k /v $v 2>&1) -join ' '
    return ($o -replace '\s+', ' ').Trim()
}

Write-Output '==== BEFORE ===='
foreach ($p in @(@($ts, 'TSAppCompat'), @($ts, 'AllowListenersInsideArgonContainer'), @($cv, 'EditionID'))) {
    Write-Output ("  " + (Rq $p[0] $p[1]))
}
Write-Output ("  Hvsi: " + ((& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Control\Hvsi' 2>&1) -join ' '))

Write-Output '==== APPLY ===='
$adds = @(
    @($ts,  'TSAppCompat', 'REG_DWORD', '1'),
    @($ts,  'AllowListenersInsideArgonContainer', 'REG_DWORD', '1'),
    @($ts,  'fDenyTSConnections', 'REG_DWORD', '0'),
    @($ts,  'StartRCM', 'REG_DWORD', '1'),
    @($rdp, 'AllowListenersInsideArgonContainer', 'REG_DWORD', '1'),
    @($rdp, 'SecurityLayer', 'REG_DWORD', '0'),
    @($rdp, 'UserAuthentication', 'REG_DWORD', '0'),
    @($rdp, 'fEnableWinStation', 'REG_DWORD', '1'),
    @($rdp, 'PortNumber', 'REG_DWORD', '3389'),
    @($cv,  'EditionID', 'REG_SZ', 'ContainerOSPlus'),
    @($cv,  'AllowListenersInsideArgonContainer', 'REG_DWORD', '1')
)
foreach ($a in $adds) { Write-Output ('  ' + ((& reg.exe add $a[0] /v $a[1] /t $a[2] /d $a[3] /f 2>&1) -join ' ')) }
Write-Output ('  ' + ((& reg.exe add 'HKLM\SYSTEM\CurrentControlSet\Control\Hvsi' /v IsHvsiContainer /t REG_DWORD /d 1 /f 2>&1) -join ' '))

Write-Output '==== API PROBE ===='
$src = @'
using System;
using System.Runtime.InteropServices;
public static class Nt {
  [DllImport("ntdll.dll", SetLastError=true)] public static extern int RtlGetCurrentServiceSessionId();
  [DllImport("kernelbase.dll", SetLastError=true)] public static extern bool WTSIsServerContainer();
}
'@
try {
    Add-Type -TypeDefinition $src -ErrorAction Stop
    Write-Output ("  RtlGetCurrentServiceSessionId = " + [Nt]::RtlGetCurrentServiceSessionId())
    Write-Output ("  WTSIsServerContainer         = " + [Nt]::WTSIsServerContainer())
}
catch { Write-Output ("  Add-Type failed: " + $_.Exception.Message) }

Write-Output '==== RESTART TermService ===='
Write-Output ('  ' + ((& sc.exe config TermService start= auto 2>&1) -join ' '))
Write-Output ('  ' + ((& sc.exe query TermService 2>&1 | Select-String 'STATE') -join ' '))
Write-Output ('  stop: ' + ((& sc.exe stop TermService 2>&1) -join ' '))
Start-Sleep -Seconds 4
Write-Output ('  start: ' + ((& sc.exe start TermService 2>&1) -join ' '))
Start-Sleep -Seconds 8
Write-Output ('  after: ' + ((& sc.exe query TermService 2>&1 | Select-String 'STATE') -join ' '))
Write-Output ('  UmRdp: ' + ((& sc.exe start UmRdpService 2>&1) -join ' '))
Write-Output ('  UmRdp: ' + ((& sc.exe query UmRdpService 2>&1 | Select-String 'STATE') -join ' '))

Write-Output '==== qwinsta ===='
(& qwinsta.exe 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }

Write-Output '==== RDS pipes ===='
try {
    (Get-ChildItem '\\.\pipe\' -ErrorAction Stop | Where-Object { $_.Name -match 'Term|RDP|Sess|Ctx|Um' } |
        Select-Object -ExpandProperty Name) | ForEach-Object { Write-Output ('  ' + $_) }
}
catch { Write-Output ('  pipe enum failed: ' + $_.Exception.Message) }

Write-Output '==== RCM log ===='
try {
    Get-WinEvent -LogName 'Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational' -MaxEvents 14 -ErrorAction Stop |
        ForEach-Object { Write-Output ('  ' + $_.TimeCreated.ToString('HH:mm:ss') + ' #' + $_.Id + ' ' + (($_.Message -replace "`r?`n", ' ').Trim())) }
}
catch { Write-Output ('  no log: ' + $_.Exception.Message) }
