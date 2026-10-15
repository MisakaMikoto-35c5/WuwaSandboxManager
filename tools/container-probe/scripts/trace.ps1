$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\trace.log"
"==== trace $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base  = "C:\ProgramData\Microsoft\Windows\Containers\Layers\cf0776b2-3566-4743-90e5-6f0db1f2064c"
New-Item -ItemType Directory -Force C:\wsmprobe | Out-Null

& logman stop wsmhcs -ets 2>$null | Out-Null
$prov = @(
  '"{17103E3F-3C6E-4677-BB17-3B267EB5BE57}"' , # Hyper-V-Compute
  '"{AF7FD3A7-B248-460C-A9F5-FEC39EF8468C}"' , # ComputeLib
  '"{45F54D37-2377-4B64-B396-370E31ACB204}"'   # ComputeCExec
)
& logman create trace wsmhcs -p $prov[0] 0xffffffffffffffff 5 -p $prov[1] 0xffffffffffffffff 5 -p $prov[2] 0xffffffffffffffff 5 -o C:\wsmprobe\hcs.etl -ft 1 -ets 2>&1 | Add-Content $log
Start-Sleep -Seconds 2

$scratch = "C:\wsmprobe\t1"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe lsandbox $scratch $base 2>&1 | Add-Content $log
& $exe lactivate $scratch 2>&1 | Add-Content $log
$nameGuid = (& $exe lname $base).Trim()
$obj = [ordered]@{ SystemType="Container"; Name="wsmprobe"; Owner="WsmProbe"; LayerFolderPath=$scratch; Layers=@(@{Id=$nameGuid;Path=$base}); HvPartition=$false; TerminateOnLastHandleClosed=$true }
$f = "$env:TEMP\opencode\wsmdiag\t1.json"
Set-Content -Path $f -Value ($obj|ConvertTo-Json -Depth 10 -Compress) -Encoding UTF8
& $exe create "wsmprobe-trace1" $f 2>&1 | Add-Content $log
Start-Sleep -Seconds 2

& logman stop wsmhcs -ets 2>&1 | Add-Content $log
Start-Sleep -Seconds 1
& tracerpt C:\wsmprobe\hcs.etl -of XML -o C:\wsmprobe\hcs.xml -y 2>&1 | Add-Content $log
Add-Content $log "== xml size =="
(Get-Item C:\wsmprobe\hcs.xml).Length | Add-Content $log
Add-Content $log "== matching lines =="
Select-String -Path C:\wsmprobe\hcs.xml -Pattern 'ersion|ismatch|CurrentBuild|26100|26200|26300|uild' -AllMatches 2>$null | Select-Object -First 80 -ExpandProperty Line | Add-Content $log
Add-Content $log "==== done ===="
