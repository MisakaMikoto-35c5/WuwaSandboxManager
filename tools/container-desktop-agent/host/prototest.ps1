$ErrorActionPreference='Continue'
Write-Output ('  rdpendp present = ' + (Test-Path "$env:SystemRoot\system32\rdpendp.dll"))
$t=[Type]::GetTypeFromCLSID([Guid]'{5828227C-20CF-4408-B73F-73AB70B8849F}')
try { $o=[Activator]::CreateInstance($t); Write-Output ('  activation OK: ' + $o.GetType().FullName) }
catch { Write-Output ('  activation FAILED: ' + $_.Exception.Message) }
