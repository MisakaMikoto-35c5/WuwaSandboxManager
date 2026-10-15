$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\validate4.log"
"==== validate4 $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$env:WSM_CONTAINER_BASE_IMAGE = 'C:\wsmprobe\img\base1'
$create = (& $exe create --name ctest5 --engine container --memory 4096 --cpu 2 --networking enable *>&1 | Out-String)
Add-Content $log $create
$m = [regex]::Match($create, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
if (-not $m.Success) { Add-Content $log "no id"; exit }
$id = $m.Value
$hp = Start-Process $exe -ArgumentList 'host', $id -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\opencode\wsmdiag\host4.out" -RedirectStandardError "$env:TEMP\opencode\wsmdiag\host4.err"
Start-Sleep -Seconds 40
Add-Content $log "== host.err =="
Add-Content $log (Get-Content "$env:TEMP\opencode\wsmdiag\host4.err" -Raw)
Add-Content $log "== ip =="
$ipJson = (& $exe ip $id *>&1 | Out-String)
Add-Content $log $ipJson
$ip = ($ipJson | ConvertFrom-Json | Select-Object -First 1)
Add-Content $log "parsed ip=$ip"
if ($ip) {
  Add-Content $log ("ping: " + ((ping -n 1 $ip | Select-String 'Reply|timed out') -join '; '))
  Add-Content $log ("tcp135: " + (Test-NetConnection -ComputerName $ip -Port 135 -WarningAction SilentlyContinue).TcpTestSucceeded)
}
Add-Content $log "== hns endpoints (names/ips) =="
$hns = & 'C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe' hns GET "/endpoints/" "" 2>&1 | Out-String
($hns -split '},') | Where-Object { $_ -match 'wsm-container|IPAddress' } | Select-Object -First 5 | Add-Content $log
Add-Content $log "== stop =="
& $exe stop $id *>&1 | Add-Content $log
Start-Sleep -Seconds 3
if (-not $hp.HasExited) { $hp.Kill() }
Add-Content $log "==== done ===="
