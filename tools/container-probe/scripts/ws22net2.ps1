$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\ws22net2.log"
"==== ws22net2 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base1 = 'C:\wsmprobe\img\base1'
$dir = "$env:TEMP\opencode\wsmdiag"
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee00ae"
$scratch = "C:\wsmprobe\ws22net2"
$netId = "C08CB7B8-9B3C-408E-8E30-5E16A3AEB444"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe argonnet $id $scratch $base1 $netId "cmd.exe /c whoami && ipconfig" 0 2>&1 | Add-Content $log
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
