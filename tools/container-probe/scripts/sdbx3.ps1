$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\sdbx3.log"
"==== sdbx3 $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$probe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$dir = "$env:TEMP\opencode\wsmdiag"
Remove-Item Env:\WSM_CONTAINER_BASE_IMAGE -ErrorAction SilentlyContinue

# Remove the wsm-nat network so the engine falls back to the ICS Default Switch.
$out = (& $probe hns GET "/networks/" "" 2>&1) -join "`n"
$json = ($out -split "`n" | Select-Object -Skip 1) -join "`n"
$nets = ($json | ConvertFrom-Json).Output
foreach ($n in $nets) {
  if ($n.Type -eq 'NAT') {
    Add-Content $log "deleting NAT network $($n.Name) $($n.ID)"
    & $probe hns DELETE "/networks/$($n.ID)" "" 2>&1 | Add-Content $log
  }
}
Start-Sleep -Seconds 3
Add-Content $log "== networks now =="
Add-Content $log (((& $probe hns GET "/networks/" "" 2>&1) -join "`n") -split '"Name":"' | Select-Object -Skip 1 | ForEach-Object { ($_ -split '"')[0] }) 

Add-Content $log "== create (tcp transport) =="
$create = (& $exe create --name sdtest3 --networking enable --transport tcp --memory 4096 --cpu 2 *>&1 | Out-String)
Add-Content $log $create
$m = [regex]::Match($create, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
if (-not $m.Success) { Add-Content $log "no id"; Add-Content $log "==== done ===="; exit }
$id = $m.Value

$hp = Start-Process $exe -ArgumentList 'host', $id -PassThru -NoNewWindow -RedirectStandardOutput "$dir\sdbx3-host.out" -RedirectStandardError "$dir\sdbx3-host.err"
Add-Content $log "waiting 180s..."
Start-Sleep -Seconds 180
Add-Content $log "-- host.out --"
Add-Content $log (Get-Content "$dir\sdbx3-host.out" -Raw -ErrorAction SilentlyContinue)
Add-Content $log "-- host.err --"
Add-Content $log (Get-Content "$dir\sdbx3-host.err" -Raw -ErrorAction SilentlyContinue)

Add-Content $log "== hcsdiag list =="
& hcsdiag list 2>&1 | Add-Content $log

$ic = (& $probe sh $id "cmd.exe /c ipconfig" 2>&1) -join "`n"
Add-Content $log "== guest ipconfig =="
Add-Content $log $ic
$ip = $null
$mi = [regex]::Match($ic, 'IPv4 Address[^:]*:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
if ($mi.Success) { $ip = $mi.Groups[1].Value }
Add-Content $log "guest ip=$ip"
if ($ip) {
  Add-Content $log ("tcp 3389: " + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  Add-Content $log ((& $probe rprobe $ip 3389 2>&1) -join "`n")
}

Add-Content $log "== stop =="
& $exe stop $id *>&1 | Add-Content $log
Start-Sleep -Seconds 5
if (-not $hp.HasExited) { $hp.Kill() }
Add-Content $log "==== done ===="
