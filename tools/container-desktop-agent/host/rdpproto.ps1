$ErrorActionPreference = 'Continue'
$rdpProto = '{5828227C-20CF-4408-B73F-73AB70B8849F}'
$other = '{E99FA5E4-A1C9-4495-8BF0-D0972D58E841}'

Write-Output "==== CLSID $rdpProto (RDP-Tcp LoadableProtocol_Object) ===="
(& reg.exe query "HKLM\SOFTWARE\Classes\CLSID\$rdpProto" /s 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }
Write-Output "==== CLSID $other ===="
(& reg.exe query "HKLM\SOFTWARE\Classes\CLSID\$other" /s 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }

Write-Output '==== protocol DLLs ===='
foreach ($f in 'rdpcorets.dll', 'rdpbase.dll', 'rdpserverbase.dll', 'rdpudd.dll', 'rdpendp.dll', 'mvmbuspipe.dll') {
    $p = "$env:SystemRoot\System32\$f"
    $ok = Test-Path $p
    Write-Output ("  {0,-20} exists={1} ver={2}" -f $f, $ok, $(if ($ok) { (Get-Item $p).VersionInfo.FileVersion } else { '-' }))
}

Write-Output '==== try to activate the protocol object ===='
try {
    $t = [Type]::GetTypeFromCLSID([Guid]$rdpProto)
    Write-Output ("  GetTypeFromCLSID -> " + $t)
    $o = [Activator]::CreateInstance($t)
    Write-Output ("  CreateInstance OK -> " + $o.GetType().FullName)
}
catch { Write-Output ('  activation FAILED: ' + $_.Exception.Message) }

Write-Output '==== GetProtocolExtension-ish: RDP-Tcp LoadableProtocol_Object value ===='
(& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp' /v LoadableProtocol_Object 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }

Write-Output '==== listener subkeys under WinStations (all) ===='
(& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations' 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }

Write-Output '==== services: TermService / SessionEnv / RDPDR / UmRdp ===='
foreach ($s in 'TermService', 'SessionEnv', 'RDPDR', 'UmRdpService', 'RdpVideoMiniport') {
    $st = (& sc.exe query $s 2>&1 | Select-String 'STATE') -join ' '
    Write-Output ("  {0,-18} {1}" -f $s, ($st -replace '\s+', ' ').Trim())
}
