$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\sdbx2.log"
"==== sdbx2 $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$probe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$dir = "$env:TEMP\opencode\wsmdiag"
Remove-Item Env:\WSM_CONTAINER_BASE_IMAGE -ErrorAction SilentlyContinue

Add-Content $log "== create (pipe default) =="
$create = (& $exe create --name sdtest2 --networking enable --memory 4096 --cpu 2 *>&1 | Out-String)
Add-Content $log $create
$m = [regex]::Match($create, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
if (-not $m.Success) { Add-Content $log "no id"; Add-Content $log "==== done ===="; exit }
$id = $m.Value

$hp = Start-Process $exe -ArgumentList 'host', $id -PassThru -NoNewWindow -RedirectStandardOutput "$dir\sdbx2-host.out" -RedirectStandardError "$dir\sdbx2-host.err"
Add-Content $log "waiting 150s for boot..."
Start-Sleep -Seconds 150
Add-Content $log "-- host.out --"
Add-Content $log (Get-Content "$dir\sdbx2-host.out" -Raw -ErrorAction SilentlyContinue)
Add-Content $log "-- host.err --"
Add-Content $log (Get-Content "$dir\sdbx2-host.err" -Raw -ErrorAction SilentlyContinue)

Add-Content $log "== guest ipconfig =="
$ic = (& $probe sh $id "cmd.exe /c ipconfig" 2>&1) -join "`n"
Add-Content $log $ic

Add-Content $log "== guest rdp listener =="
Set-Content "$dir\g-rdp.cmd" -Value 'cmd.exe /c reg query "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v PortNumber' -Encoding ASCII
Add-Content $log ((& $probe shf $id "$dir\g-rdp.cmd" 2>&1) -join "`n")

Add-Content $log "== named pipe exists =="
Add-Content $log ("pipe: " + (Test-Path "\\.\pipe\$id"))

# Try to make the guest listen on TCP (mstsc) and probe it.
$ip = $null
$mi = [regex]::Match($ic, 'IPv4 Address[^:]*:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
if ($mi.Success) { $ip = $mi.Groups[1].Value }
Add-Content $log "guest ip=$ip"

if ($ip) {
  Add-Content $log "== tcp 3389 =="
  Add-Content $log ("tcp: " + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  Add-Content $log ((& $probe rprobe $ip 3389 2>&1) -join "`n")
}

Add-Content $log "== stop =="
& $exe stop $id *>&1 | Add-Content $log
Start-Sleep -Seconds 5
if (-not $hp.HasExited) { $hp.Kill() }
Add-Content $log "==== done ===="
