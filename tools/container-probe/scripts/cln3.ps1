$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\cln3.log"
"==== cln3 ====" | Set-Content $log
$layer = 'C:\wsm-images\server-ltsc2022'
Add-Content $log "== disks/volumes referencing the layer =="
Get-Disk -ErrorAction SilentlyContinue | Where-Object { $_.Location -like "*server-ltsc2022*" } | Select-Object Number,FriendlyName,Location,OperationalStatus | Out-String | Add-Content $log
Get-Volume -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*server-ltsc2022*" } | Out-String | Add-Content $log
Add-Content $log "== children =="
Get-ChildItem $layer -Force -ErrorAction SilentlyContinue | Select-Object Name,Attributes | Out-String | Add-Content $log
Add-Content $log "== rmdir attempt =="
$out = & cmd.exe /c "rmdir /s /q `"$layer`"" 2>&1
Add-Content $log ($out -join "`n")
Add-Content $log ("exists: " + (Test-Path $layer))
Add-Content $log "== remaining children =="
Get-ChildItem $layer -Recurse -Force -ErrorAction SilentlyContinue | Select-Object -First 20 -ExpandProperty FullName | Add-Content $log
