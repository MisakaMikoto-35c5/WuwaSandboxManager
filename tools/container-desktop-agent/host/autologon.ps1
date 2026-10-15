# Can we give a SAM account a password, and does a container registry write land
# in the image layer (and therefore survive a container restart)?
$ErrorActionPreference = 'Continue'

$user = 'Administrator'
$password = 'Wsm!Passw0rd'

Write-Output '---- net user <user> <password> ----'
(& net.exe user $user $password 2>&1) -join "`n"
Write-Output ('exit=' + $LASTEXITCODE)

Write-Output '---- account state ----'
(& net.exe user $user 2>&1 | Select-String 'Account active|Password required|Password last set|Local Group' | ForEach-Object { $_.Line.Trim() }) -join "`n"

Write-Output '---- write the Winlogon auto-logon values ----'
$winlogon = 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'
$values = @(
    @('AutoAdminLogon', '1'),
    @('DefaultUserName', $user),
    @('DefaultDomainName', $env:COMPUTERNAME),
    @('DefaultPassword', $password),
    @('ForceAutoLogon', '1'),
    @('Shell', 'C:\wsm\agent.cmd')
)
foreach ($pair in $values) {
    (& reg.exe add $winlogon /v $pair[0] /t REG_SZ /d $pair[1] /f 2>&1) -join ' '
}

Write-Output '---- read back ----'
(& reg.exe query $winlogon 2>&1 |
    Select-String 'AutoAdminLogon|DefaultUser|DefaultDomain|DefaultPass|ForceAutoLogon|Shell|Userinit|UIHost' |
    ForEach-Object { $_.Line.Trim() }) -join "`n"

Write-Output '---- probe marker (to check image persistence after a restart) ----'
(& reg.exe add 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' /v WsmProbeMarker /t REG_SZ /d ('marker-' + (Get-Date -Format 'HHmmss')) /f 2>&1) -join ' '
(& reg.exe query $winlogon /v WsmProbeMarker 2>&1 | ForEach-Object { $_.Line.Trim() }) -join "`n"
