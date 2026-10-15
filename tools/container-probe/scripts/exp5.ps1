$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\exp5.log"
"==== exp5 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base = "C:\ProgramData\Microsoft\Windows\Containers\Layers\cf0776b2-3566-4743-90e5-6f0db1f2064c"
$top  = "C:\ProgramData\Microsoft\Windows\Containers\Layers\6a50e60e-b4b0-45cc-abc7-87d84f6551ea"
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0020"
$scratch = "C:\wsmprobe\cs20"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue

Add-Content $log "== argon chain =="
& $exe argon $id $scratch "$base,$top" "cmd.exe /c whoami" 2>&1 | Add-Content $log
Add-Content $log "== stop =="
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
