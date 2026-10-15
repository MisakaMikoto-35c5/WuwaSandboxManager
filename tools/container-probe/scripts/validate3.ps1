$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\validate3.log"
"==== validate3 $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$env:WSM_CONTAINER_BASE_IMAGE = 'C:\wsmprobe\img\base1'

$create = (& $exe create --name ctest3 --engine container --memory 4096 --cpu 2 --networking enable *>&1 | Out-String)
Add-Content $log $create
$m = [regex]::Match($create, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
if (-not $m.Success) { Add-Content $log "no id"; exit }
$id = $m.Value
Add-Content $log "id=$id"

$hp = Start-Process $exe -ArgumentList 'host', $id -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\opencode\wsmdiag\host3.out" -RedirectStandardError "$env:TEMP\opencode\wsmdiag\host3.err"
Start-Sleep -Seconds 35
Add-Content $log "-- host.out --"
Add-Content $log (Get-Content "$env:TEMP\opencode\wsmdiag\host3.out" -Raw)
Add-Content $log "== exec whoami =="
& $exe exec $id cmd.exe /c whoami *>&1 | Add-Content $log
Add-Content $log "== exec ver =="
& $exe exec $id cmd.exe /c ver *>&1 | Add-Content $log
Add-Content $log "== run key patch + start spooler test =="
& $exe exec $id cmd.exe /c "echo hello" *>&1 | Add-Content $log
Add-Content $log "== ip =="
& $exe ip $id *>&1 | Add-Content $log
Add-Content $log "== stop =="
& $exe stop $id *>&1 | Add-Content $log
Start-Sleep -Seconds 3
if (-not $hp.HasExited) { $hp.Kill() }
Add-Content $log "==== done ===="
