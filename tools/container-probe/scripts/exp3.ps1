$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\exp3.log"
"==== exp3 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$layerId = "6a50e60e-b4b0-45cc-abc7-87d84f6551ea"
$layerRoot = "C:\ProgramData\Microsoft\Windows\Containers\Layers\$layerId"
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0010"
$scratch = "C:\wsmprobe\cs1"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue

Add-Content $log "== lname =="
& $exe lname $layerRoot 2>&1 | Add-Content $log
Add-Content $log "== lmount =="
& $exe lmount $layerRoot 2>&1 | Add-Content $log
Add-Content $log "== argon single parent =="
& $exe argon $id $scratch $layerRoot "cmd.exe /c whoami" 2>&1 | Add-Content $log
Add-Content $log "== props =="
& $exe props $id 2>&1 | Add-Content $log
Add-Content $log "== stop =="
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
