$ErrorActionPreference = 'Continue'
$rdpProto = '{5828227C-20CF-4408-B73F-73AB70B8849F}'

Write-Output '==== environment ===='
foreach ($n in 'windir', 'SystemRoot', 'SystemDrive', 'PATH', 'COMPUTERNAME', 'USERNAME') {
    $v = [Environment]::GetEnvironmentVariable($n, 'Machine')
    $p = [Environment]::GetEnvironmentVariable($n, 'Process')
    Write-Output ("  {0,-12} machine=[{1}] process=[{2}]" -f $n, $v, $p)
}

Write-Output '==== HKLM Session Manager\Environment ===='
(& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment' 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }

Write-Output '==== does the file exist / is it loadable? ===='
$path = 'C:\Windows\system32\rdpcorets.dll'
Write-Output ("  exists=" + (Test-Path $path) + " size=" + (Get-Item $path -ErrorAction SilentlyContinue).Length)
$src = @'
using System;
using System.Runtime.InteropServices;
public static class K {
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  public static extern IntPtr LoadLibraryW(string p);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern uint GetLastError();
}
'@
try {
    Add-Type -TypeDefinition $src -ErrorAction Stop
    $h = [K]::LoadLibraryW($path)
    Write-Output ("  LoadLibraryW(rdpcorets) handle=" + $h + " err=" + [K]::GetLastError())
}
catch { Write-Output ('  Add-Type failed: ' + $_.Exception.Message) }

Write-Output '==== COM activation attempts ===='
$t = [Type]::GetTypeFromCLSID([Guid]$rdpProto)
try { $null = [Activator]::CreateInstance($t); Write-Output '  default env: OK' }
catch { Write-Output ('  default env: FAILED ' + $_.Exception.Message) }

# force windir/SystemRoot and retry (REG_EXPAND_SZ uses the process env)
[Environment]::SetEnvironmentVariable('windir', 'C:\Windows', 'Process')
[Environment]::SetEnvironmentVariable('SystemRoot', 'C:\Windows', 'Process')
try { $null = [Activator]::CreateInstance($t); Write-Output '  with windir set: OK' }
catch { Write-Output ('  with windir set: FAILED ' + $_.Exception.Message) }

Write-Output '==== could it be the registry value itself? ===='
(& reg.exe query 'HKLM\SOFTWARE\Classes\CLSID\{5828227C-20CF-4408-B73F-73AB70B8849F}\InprocServer32' /ve 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }
Write-Output ('  HKLM HKLM\SOFTWARE\Classes\CLSID exists? ' + (Test-Path 'HKLM:\SOFTWARE\Classes\CLSID\{5828227C-20CF-4408-B73F-73AB70B8849F}'))
Write-Output '==== UmRdp service image path + umrdp.dll ===='
(& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Services\UmRdpService' 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }
Write-Output ('  umrdp.dll exists=' + (Test-Path 'C:\Windows\system32\umrdp.dll'))
