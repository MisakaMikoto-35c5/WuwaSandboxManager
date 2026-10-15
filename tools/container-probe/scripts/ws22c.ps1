$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\ws22c.log"
"==== ws22c $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base1 = 'C:\wsmprobe\img\base1'

$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee00ac"
$scratch = "C:\wsmprobe\ws22c"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe argon $id $scratch $base1 "cmd.exe /c whoami && ver" 2>&1 | Add-Content $log
Add-Content $log "== list =="
& $exe list 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
