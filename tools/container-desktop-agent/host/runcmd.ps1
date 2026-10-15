$ErrorActionPreference='Continue'
$o = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -Session 4 -Desktop 'winsta0\Winlogon' -CommandLine 'cmd.exe /c C:\wsm\diag.cmd' 2>&1
$o | Out-String | Write-Output
Start-Sleep -Seconds 20
foreach ($f in 'C:\wsm\j1.out','C:\wsm\j1.err','C:\wsm\j1.done','C:\wsm\j1.log','C:\wsm\j2.log') {
  Write-Output ("---- {0} (size={1})" -f $f, (Get-Item $f -ErrorAction SilentlyContinue).Length)
  if (Test-Path $f) { (Get-Content $f) | ForEach-Object { Write-Output ('  ' + $_) } }
}
Write-Output ('j1.png size=' + (Get-Item C:\wsm\j1.png -ErrorAction SilentlyContinue).Length)
