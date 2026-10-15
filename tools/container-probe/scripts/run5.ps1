$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\run5.log"
"==== run5 $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$probe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$dir = "$env:TEMP\opencode\wsmdiag"
$id = 'b891c91f-5848-40bf-a625-23c8ddf95e78'

$child = "& '$exe' host $id *> '$dir\run5-host.out'"
Set-Content "$dir\run5-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -WindowStyle Hidden -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\run5-child.ps1" | Out-Null

Add-Content $log "waiting 200s..."
Start-Sleep -Seconds 200
Add-Content $log "-- host.out --"
Add-Content $log (Get-Content "$dir\run5-host.out" -Raw -ErrorAction SilentlyContinue)

Add-Content $log "== wsm ip =="
Add-Content $log ((& $exe ip $id *>&1 | Out-String))

Add-Content $log "== guest ipconfig =="
$ic = (& $probe sh $id "cmd.exe /c ipconfig" 2>&1) -join "`n"
Add-Content $log $ic
$ip = $null
$mi = [regex]::Match($ic, 'IPv4 Address[^:]*:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
if ($mi.Success) { $ip = $mi.Groups[1].Value }
if ($ip) {
  Add-Content $log "guest ip=$ip"
  Add-Content $log ("tcp 3389: " + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  Add-Content $log ((& $probe rprobe $ip 3389 2>&1) -join "`n")
  Set-Content "$dir\wsm-desktop.rdp" -Value "full address:s:$ip`nusername:s:WDAGUtilityAccount`nprompt for credentials:i:1`nauthentication level:i:0`nenablecredsspSupport:i:0" -Encoding UTF8
  Add-Content $log "mstsc /v:$ip"
}
Add-Content $log "== left running =="
Add-Content $log "==== done ===="
