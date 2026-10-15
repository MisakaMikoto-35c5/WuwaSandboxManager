$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\ws22net6.log"
"==== ws22net6 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base1 = 'C:\wsmprobe\img\base1'
$natId = (Get-HnsNetwork | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1).ID
Add-Content $log "natId=$natId"

$env:WSMPROBE_NOPREP = "1"
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee00b2"
$scratch = "C:\wsmprobe\ws22net6"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe argonnet $id $scratch $base1 $natId "cmd.exe /c whoami && ipconfig" 0 2>&1 | Add-Content $log
& $exe stop $id 2>&1 | Add-Content $log

Add-Content $log "== schema2 + endpoint id =="
# create an HNS endpoint in the NAT network and try schema2 NetworkAdapters
$epBody = "{`"VirtualNetwork`":`"$natId`"}"
$id2 = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee00b3"
$scratch2 = "C:\wsmprobe\ws22net6b"
Remove-Item -Recurse -Force $scratch2 -ErrorAction SilentlyContinue
Remove-Item Env:\WSMPROBE_NOPREP -ErrorAction SilentlyContinue
& $exe argonnet $id2 $scratch2 $base1 $natId "cmd.exe /c whoami" 0 2>&1 | Add-Content $log
& $exe stop $id2 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
