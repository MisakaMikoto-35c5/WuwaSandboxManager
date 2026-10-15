$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\ws22b.log"
"==== ws22b $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base1 = 'C:\wsmprobe\img\base1'

Add-Content $log "== before: layout =="
Get-ChildItem $base1 -Force | Select-Object Name | Out-String | Add-Content $log

Add-Content $log "== processbase =="
& $exe lprocessbase $base1 2>&1 | Add-Content $log

Add-Content $log "== after: layout =="
Get-ChildItem $base1 -Force | Select-Object Name,LinkType | Format-Table -AutoSize | Out-String | Add-Content $log
Add-Content $log ("Hives exists: " + (Test-Path "$base1\Hives"))
Get-ChildItem "$base1\Hives" -Force -ErrorAction SilentlyContinue | Select-Object Name,LinkType | Format-Table -AutoSize | Out-String | Add-Content $log

Add-Content $log "== create sandbox from base1 =="
$scratch = "C:\wsmprobe\ws22b"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe lsandbox $scratch $base1 2>&1 | Add-Content $log

Add-Content $log "== argon =="
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee00ab"
& $exe argon $id "C:\wsmprobe\ws22bb" $base1 "cmd.exe /c whoami && ver" 2>&1 | Add-Content $log
Add-Content $log "== stop =="
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
