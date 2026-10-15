# Host-side, elevated: stop every container, then read the image hives offline to
# see whether the container's registry writes persisted into the image layer.
$ErrorActionPreference = 'Continue'

$image = 'C:\wsm-images\servercore-ltsc2022'

Write-Output '---- stop container probes ----'
Get-Process hcsprobe -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Output ('  killing hcsprobe pid ' + $_.Id)
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 8

Write-Output '---- SOFTWARE hive: Winlogon ----'
$software = "$image\Files\Windows\System32\config\SOFTWARE"
& reg.exe unload HKLM\WSMCHK 2>&1 | Out-Null
$load = (& reg.exe load HKLM\WSMCHK $software 2>&1) -join ' '
Write-Output ('load: ' + $load)
if ($load -notmatch 'ERROR') {
    (& reg.exe query 'HKLM\WSMCHK\Microsoft\Windows NT\CurrentVersion\Winlogon' 2>&1 |
        Select-String 'AutoAdminLogon|DefaultUser|DefaultDomain|DefaultPass|ForceAutoLogon|Shell|WsmProbeMarker|UIHost' |
        ForEach-Object { '  ' + $_.Line.Trim() }) -join "`n"
    Write-Output '  -- all values on the host Winlogon key (for hook names) --'
    (& reg.exe query 'HKLM\WSMCHK\Microsoft\Windows NT\CurrentVersion\Winlogon' 2>&1 |
        Where-Object { $_ -match 'REG_' } | ForEach-Object { '  ' + $_.Trim() }) -join "`n"
    & reg.exe unload HKLM\WSMCHK 2>&1 | Out-Null
}

Write-Output '---- SYSTEM hive: sessions ----'
$system = "$image\Files\Windows\System32\config\SYSTEM"
& reg.exe unload HKLM\WSMSYS 2>&1 | Out-Null
$load2 = (& reg.exe load HKLM\WSMSYS $system 2>&1) -join ' '
Write-Output ('load: ' + $load2)
if ($load2 -notmatch 'ERROR') {
    (& reg.exe query 'HKLM\WSMSYS\ControlSet001\Control\Session Manager' /v NumberOfInitialSessions 2>&1 | ForEach-Object { '  ' + $_.Trim() }) -join "`n"
    & reg.exe unload HKLM\WSMSYS 2>&1 | Out-Null
}

Write-Output '---- SAM size (did net user change it?) ----'
$sam = "$image\Files\Windows\System32\config\SAM"
Write-Output ('  SAM: ' + (Get-FileHash $sam -Algorithm SHA256).Hash.Substring(0, 16) + ' (' + (Get-Item $sam).Length + ' bytes, last write ' + (Get-Item $sam).LastWriteTime + ')')
