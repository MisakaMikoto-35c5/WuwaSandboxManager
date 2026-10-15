$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\image-full.log"
"==== image-full $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$layer = 'C:\wsm-images\servercore-ltsc2022'
$env:WSM_CONTAINER_BASE_IMAGE = $layer

Add-Content $log "== import =="
& $exe image import 'mcr.microsoft.com/windows/servercore:ltsc2022' $layer *>&1 | Add-Content $log

Add-Content $log "== layout =="
Get-ChildItem $layer -Force -ErrorAction SilentlyContinue | Select-Object Name | Out-String | Add-Content $log

Add-Content $log "== create =="
$create = (& $exe create --name simg --engine container --memory 4096 --cpu 2 --networking enable *>&1 | Out-String)
Add-Content $log $create
$m = [regex]::Match($create, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
if (-not $m.Success) { Add-Content $log "no id"; Add-Content $log "==== done ===="; exit }
$id = $m.Value

Add-Content $log "== host =="
$hp = Start-Process $exe -ArgumentList 'host', $id -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\opencode\wsmdiag\img-host.out" -RedirectStandardError "$env:TEMP\opencode\wsmdiag\img-host.err"
Start-Sleep -Seconds 45
Add-Content $log "-- host.out --"
Add-Content $log (Get-Content "$env:TEMP\opencode\wsmdiag\img-host.out" -Raw)
Add-Content $log "-- host.err --"
Add-Content $log (Get-Content "$env:TEMP\opencode\wsmdiag\img-host.err" -Raw)
Add-Content $log "== exec ver =="
& $exe exec $id cmd.exe /c ver *>&1 | Add-Content $log
Add-Content $log "== ip =="
& $exe ip $id *>&1 | Add-Content $log
Add-Content $log "== stop =="
& $exe stop $id *>&1 | Add-Content $log
Start-Sleep -Seconds 3
if (-not $hp.HasExited) { $hp.Kill() }
Add-Content $log "==== done ===="
