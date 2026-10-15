$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\validate2.log"
"==== validate $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$env:WSM_CONTAINER_BASE_IMAGE = 'C:\wsmprobe\img\base1'

Add-Content $log "== env =="
& $exe env *>&1 | Add-Content $log

Add-Content $log "== create =="
$create = (& $exe create --name ctest2 --engine container --memory 4096 --cpu 2 --networking enable *>&1 | Out-String)
Add-Content $log $create
$m = [regex]::Match($create, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
if (-not $m.Success) { Add-Content $log "no id; abort"; Add-Content $log "==== done ===="; exit }
$id = $m.Value
Add-Content $log "id=$id"

Add-Content $log "== host (background) =="
$hostProc = Start-Process $exe -ArgumentList 'host', $id -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\opencode\wsmdiag\host.out" -RedirectStandardError "$env:TEMP\opencode\wsmdiag\host.err"
Start-Sleep -Seconds 35
Add-Content $log "-- host.out --"
Add-Content $log (Get-Content "$env:TEMP\opencode\wsmdiag\host.out" -Raw -ErrorAction SilentlyContinue)
Add-Content $log "-- host.err --"
Add-Content $log (Get-Content "$env:TEMP\opencode\wsmdiag\host.err" -Raw -ErrorAction SilentlyContinue)

Add-Content $log "== running =="
& $exe running *>&1 | Add-Content $log

Add-Content $log "== exec whoami/ver =="
& $exe exec $id 'cmd.exe /c whoami && ver' *>&1 | Add-Content $log

Add-Content $log "== ip =="
& $exe ip $id *>&1 | Add-Content $log

Add-Content $log "== exec ipconfig =="
& $exe exec $id 'cmd.exe /c ipconfig' *>&1 | Add-Content $log

Add-Content $log "== stop =="
& $exe stop $id *>&1 | Add-Content $log
Start-Sleep -Seconds 3
if (-not $hostProc.HasExited) { $hostProc.Kill() }
Add-Content $log "==== done ===="

