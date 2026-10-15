$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\validate5.log"
"==== validate5 $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$env:WSM_CONTAINER_BASE_IMAGE = 'C:\wsmprobe\img\base1'
$create = (& $exe create --name ctest6 --engine container --memory 4096 --cpu 2 --networking enable *>&1 | Out-String)
Add-Content $log $create
$m = [regex]::Match($create, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
if (-not $m.Success) { Add-Content $log "no id"; exit }
$id = $m.Value
$hp = Start-Process $exe -ArgumentList 'host', $id -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\opencode\wsmdiag\host5.out" -RedirectStandardError "$env:TEMP\opencode\wsmdiag\host5.err"
Start-Sleep -Seconds 40
Add-Content $log "== ip (in-process, no service) =="
Add-Content $log ((& $exe ip $id *>&1 | Out-String))
Add-Content $log "== ip (forced --service) =="
Add-Content $log ((& $exe ip $id --service *>&1 | Out-String))
Add-Content $log "== stop =="
& $exe stop $id *>&1 | Add-Content $log
Start-Sleep -Seconds 3
if (-not $hp.HasExited) { $hp.Kill() }
Add-Content $log "==== done ===="
