$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\sdbx5.log"
"==== sdbx5 $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$probe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$dir = "$env:TEMP\opencode\wsmdiag"
Remove-Item Env:\WSM_CONTAINER_BASE_IMAGE -ErrorAction SilentlyContinue

# Stop leftover sandbox VMs (owner WindowsSandbox); keep CmService's template.
foreach ($line in (& hcsdiag list 2>&1)) {
  if ($line -match '^\s*([0-9a-fA-F-]{36})\s+Killed') { }
}
$list = (& hcsdiag list -raw 2>&1) -join "`n"
try {
  foreach ($vm in (($list | ConvertFrom-Json))) {
    if ($vm.Owner -eq 'WindowsSandbox') {
      Add-Content $log "stopping leftover sandbox $($vm.Id)"
      & $probe stop $vm.Id 2>&1 | Add-Content $log
    }
  }
} catch { Add-Content $log "cleanup parse err: $($_.Exception.Message)" }
Start-Sleep -Seconds 5

Add-Content $log "== create (tcp) =="
$create = (& $exe create --name sdtest5 --networking enable --transport tcp --memory 4096 --cpu 2 *>&1 | Out-String)
Add-Content $log $create
$m = [regex]::Match($create, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
if (-not $m.Success) { Add-Content $log "no id"; Add-Content $log "==== done ===="; exit }
$id = $m.Value

$child = "& '$exe' host $id *> '$dir\sdbx5-host.out'"
Set-Content "$dir\sdbx5-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -WindowStyle Hidden -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\sdbx5-child.ps1" | Out-Null

Add-Content $log "waiting 200s..."
Start-Sleep -Seconds 200
Add-Content $log "-- host.out --"
Add-Content $log (Get-Content "$dir\sdbx5-host.out" -Raw -ErrorAction SilentlyContinue)

Add-Content $log "== wsm ip =="
Add-Content $log ((& $exe ip $id *>&1 | Out-String))

Add-Content $log "== guest ipconfig =="
$ic = (& $probe sh $id "cmd.exe /c ipconfig" 2>&1) -join "`n"
Add-Content $log $ic
$ip = $null
$mi = [regex]::Match($ic, 'IPv4 Address[^:]*:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
if ($mi.Success) { $ip = $mi.Groups[1].Value }

$bytes = [System.Text.Encoding]::UTF8.GetBytes($id.ToLowerInvariant())
$hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
$hex = [System.BitConverter]::ToString($hash, 0, 10).Replace('-', '')
Add-Content $log "credentials: user=WDAGUtilityAccount password=Wsm!$hex id=$id"

if ($ip) {
  Add-Content $log "guest ip=$ip"
  Add-Content $log ("tcp 3389: " + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  Add-Content $log "== rdp handshake =="
  Add-Content $log ((& $probe rprobe $ip 3389 2>&1) -join "`n")
  Add-Content $log "connect with: mstsc /v:$ip"
}
Add-Content $log "== leaving sandbox running (id=$id) =="
Add-Content $log "==== done ===="
