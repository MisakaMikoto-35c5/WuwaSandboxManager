$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\sdbx.log"
"==== sdbx $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$probe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
Remove-Item Env:\WSM_CONTAINER_BASE_IMAGE -ErrorAction SilentlyContinue

Add-Content $log "== create (HCS/VM, tcp transport) =="
$create = (& $exe create --name sdtest --networking enable --transport tcp --memory 4096 --cpu 2 *>&1 | Out-String)
Add-Content $log $create
$m = [regex]::Match($create, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
if (-not $m.Success) { Add-Content $log "no id"; Add-Content $log "==== done ===="; exit }
$id = $m.Value

Add-Content $log "== host (background) =="
$hp = Start-Process $exe -ArgumentList 'host', $id -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\opencode\wsmdiag\sdbx-host.out" -RedirectStandardError "$env:TEMP\opencode\wsmdiag\sdbx-host.err"
Start-Sleep -Seconds 120
Add-Content $log "-- host.out --"
Add-Content $log (Get-Content "$env:TEMP\opencode\wsmdiag\sdbx-host.out" -Raw -ErrorAction SilentlyContinue)
Add-Content $log "-- host.err --"
Add-Content $log (Get-Content "$env:TEMP\opencode\wsmdiag\sdbx-host.err" -Raw -ErrorAction SilentlyContinue)

Add-Content $log "== ip =="
$ipJson = (& $exe ip $id *>&1 | Out-String)
Add-Content $log $ipJson
$ip = ($ipJson | ConvertFrom-Json | Select-Object -First 1)

if ($ip) {
  Add-Content $log "== tcp 3389 =="
  Add-Content $log ("tcp: " + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  Add-Content $log "== rdp handshake =="
  Add-Content $log ((& $probe rprobe $ip 3389 2>&1) -join "`n")
}

Add-Content $log "== stop =="
& $exe stop $id *>&1 | Add-Content $log
Start-Sleep -Seconds 5
if (-not $hp.HasExited) { $hp.Kill() }
Add-Content $log "==== done ===="
