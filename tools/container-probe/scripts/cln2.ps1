$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\cln2.log"
"==== cln2 ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$layer = 'C:\wsm-images\server-ltsc2022'
& $exe lunprepare $layer 2>&1 | Add-Content $log
& $exe ldeactivate $layer 2>&1 | Add-Content $log
& $exe ldestroy $layer 2>&1 | Add-Content $log
Remove-Item -Recurse -Force $layer -ErrorAction SilentlyContinue
Add-Content $log ("exists after: " + (Test-Path $layer))
Get-ChildItem 'C:\wsm-images' -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | Add-Content $log
Get-PSDrive C | Select-Object @{n='FreeGB';e={[math]::Round($_.Free/1GB,1)}} | Out-String | Add-Content $log
