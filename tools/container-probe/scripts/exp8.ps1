$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\exp8.log"
"==== exp8 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base = "C:\ProgramData\Microsoft\Windows\Containers\Layers\cf0776b2-3566-4743-90e5-6f0db1f2064c"
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0040"
$scratch = "C:\wsmprobe\cs50"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe argon $id $scratch $base "cmd.exe /c whoami" 2>&1 | Add-Content $log
Add-Content $log "== sh whoami =="
& $exe sh $id "cmd.exe /c whoami" 2>&1 | Add-Content $log
Add-Content $log "== sh ver =="
& $exe sh $id "cmd.exe /c ver" 2>&1 | Add-Content $log
Add-Content $log "== sh dir windows =="
& $exe sh $id "cmd.exe /c dir C:\\Windows\\System32\\config" 2>&1 | Add-Content $log
Add-Content $log "==== leaving running ===="
