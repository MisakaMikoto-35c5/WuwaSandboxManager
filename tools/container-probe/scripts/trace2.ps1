$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\trace2.log"
"==== trace2 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base  = "C:\ProgramData\Microsoft\Windows\Containers\Layers\cf0776b2-3566-4743-90e5-6f0db1f2064c"
New-Item -ItemType Directory -Force C:\wsmprobe | Out-Null
Remove-Item C:\wsmprobe\hcs.etl -Force -ErrorAction SilentlyContinue

& logman stop wsmhcs -ets 2>$null | Out-Null
Add-Content $log "create trace:"
& logman create trace wsmhcs -ets -o C:\wsmprobe\hcs.etl -p "{17103E3F-3C6E-4677-BB17-3B267EB5BE57}" 0xffffffffffffffff 5 2>&1 | Add-Content $log
Add-Content $log "add provider:"
& logman update trace wsmhcs -ets -p "{AF7FD3A7-B248-460C-A9F5-FEC39EF8468C}" 0xffffffffffffffff 5 2>&1 | Add-Content $log
& logman update trace wsmhcs -ets -p "{45F54D37-2377-4B64-B396-370E31ACB204}" 0xffffffffffffffff 5 2>&1 | Add-Content $log
Start-Sleep -Seconds 2

$scratch = "C:\wsmprobe\t2"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe lsandbox $scratch $base 2>&1 | Add-Content $log
& $exe lactivate $scratch 2>&1 | Add-Content $log
$nameGuid = (& $exe lname $base).Trim()
$obj = [ordered]@{ SystemType="Container"; Name="wsmprobe"; Owner="WsmProbe"; LayerFolderPath=$scratch; Layers=@(@{Id=$nameGuid;Path=$base}); HvPartition=$false; TerminateOnLastHandleClosed=$true }
$f = "$env:TEMP\opencode\wsmdiag\t2.json"
Set-Content -Path $f -Value ($obj|ConvertTo-Json -Depth 10 -Compress) -Encoding UTF8
& $exe create "wsmprobe-trace2" $f 2>&1 | Add-Content $log
Start-Sleep -Seconds 2

& logman stop wsmhcs -ets 2>&1 | Add-Content $log
Start-Sleep -Seconds 1
Add-Content $log ("etl size: " + (Get-Item C:\wsmprobe\hcs.etl -ErrorAction SilentlyContinue).Length)
& tracerpt C:\wsmprobe\hcs.etl -o C:\wsmprobe\hcs.xml -of XML -y 2>&1 | Add-Content $log
Add-Content $log ("xml size: " + (Get-Item C:\wsmprobe\hcs.xml -ErrorAction SilentlyContinue).Length)
Add-Content $log "== lines mentioning version/build/match =="
Select-String -Path C:\wsmprobe\hcs.xml -Pattern 'ersion|ismatch|match|26100|26200|26300|uild' 2>$null | Select-Object -First 60 -ExpandProperty Line | Add-Content $log
Add-Content $log "==== done ===="
