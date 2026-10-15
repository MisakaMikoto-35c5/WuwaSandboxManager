$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\exp6.log"
"==== exp6 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base = "C:\ProgramData\Microsoft\Windows\Containers\Layers\cf0776b2-3566-4743-90e5-6f0db1f2064c"
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0030"
$scratch = "C:\wsmprobe\cs30"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue

& $exe argon $id $scratch $base "cmd.exe /c whoami" 2>&1 | Add-Content $log
Add-Content $log "== sh dir =="
& $exe sh $id "cmd.exe /c dir C:\\" 2>&1 | Add-Content $log
Add-Content $log "== props =="
& $exe props $id 2>&1 | Add-Content $log
Add-Content $log "==== leaving running ===="
