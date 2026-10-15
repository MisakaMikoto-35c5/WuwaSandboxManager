$ErrorActionPreference='Continue'
foreach ($f in 'C:\wsm\i2.log','C:\wsm\deskinput.log','C:\wsm\dig.log','C:\wsm\Ui.dll') {
  Write-Output ("--- {0} exists={1} size={2}" -f $f, (Test-Path $f), (Get-Item $f -ErrorAction SilentlyContinue).Length)
  if (Test-Path $f) { (Get-Content $f -ErrorAction SilentlyContinue) | ForEach-Object { Write-Output ('  ' + $_) } }
}
Write-Output '--- session 4 processes ---'
(& tasklist.exe /fi "session eq 4" 2>&1) | ForEach-Object { Write-Output ('  ' + $_) }
