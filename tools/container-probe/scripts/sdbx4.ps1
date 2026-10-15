$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\sdbx4.log"
"==== sdbx4 $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$probe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$dir = "$env:TEMP\opencode\wsmdiag"
Remove-Item Env:\WSM_CONTAINER_BASE_IMAGE -ErrorAction SilentlyContinue

Add-Content $log "== create (tcp transport, VM/HCS engine) =="
$create = (& $exe create --name sdtest4 --networking enable --transport tcp --memory 4096 --cpu 2 *>&1 | Out-String)
Add-Content $log $create
$m = [regex]::Match($create, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
if (-not $m.Success) { Add-Content $log "no id"; Add-Content $log "==== done ===="; exit }
$id = $m.Value

# Launch the holder detached and LEAVE IT RUNNING so the user can mstsc in.
$child = @"
& '$exe' host $id *> '$dir\sdbx4-host.out'
"@
Set-Content "$dir\sdbx4-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb minimize -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\sdbx4-child.ps1" | Out-Null

Add-Content $log "waiting 180s for boot..."
Start-Sleep -Seconds 180
Add-Content $log "-- host.out --"
Add-Content $log (Get-Content "$dir\sdbx4-host.out" -Raw -ErrorAction SilentlyContinue)

Add-Content $log "== wsm ip =="
$ipJson = (& $exe ip $id *>&1 | Out-String)
Add-Content $log $ipJson
$ip = ($ipJson | ConvertFrom-Json | Select-Object -First 1)

Add-Content $log "== user/password =="
$bytes = [System.Text.Encoding]::UTF8.GetBytes($id)
$hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
$pw = "Wsm!" + ([System.Convert]::ToHexString($hash[0..9]))
Add-Content $log "id=$id user=WDAGUtilityAccount password=$pw"

if ($ip) {
  Add-Content $log "== tcp 3389 =="
  Add-Content $log ("tcp: " + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  Add-Content $log "== rdp handshake =="
  Add-Content $log ((& $probe rprobe $ip 3389 2>&1) -join "`n")
  Add-Content $log "== mstsc command =="
  Add-Content $log "mstsc /v:$ip"
}
Add-Content $log "== leaving sandbox running =="
Add-Content $log "==== done ===="
