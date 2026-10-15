$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\ws22net.log"
"==== ws22net $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base1 = 'C:\wsmprobe\img\base1'
$dir = "$env:TEMP\opencode\wsmdiag"

# 1. find NAT/ICS network id
$netJson = & $exe ipquery dummy 2>&1
Add-Content $log "== hns endpoints sample =="
Add-Content $log ($netJson -join "`n")

$netsRaw = & powershell -NoProfile -Command "Import-Module -Name ''HNS'' -ErrorAction SilentlyContinue; Get-HnsNetwork | ConvertTo-Json -Depth 4" 2>$null
Add-Content $log "== Get-HnsNetwork =="
Add-Content $log ($netsRaw -join "`n")

$natId = $null
try {
  $nets = Get-HnsNetwork
  $nat = $nets | Where-Object { $_.Type -in @('NAT','ICS') } | Select-Object -First 1
  if ($nat) { $natId = $nat.ID; Add-Content $log "chosen network: $($nat.Name) type=$($nat.Type) id=$natId" }
} catch { Add-Content $log "HNS module err: $($_.Exception.Message)" }

if (-not $natId) { Add-Content $log "no NAT/ICS network found; aborting"; Add-Content $log "==== done ===="; exit }

# 2. prepare scratch
$scratch = "C:\wsmprobe\ws22net"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe lsandbox $scratch $base1 2>&1 | Add-Content $log
& $exe lactivate $scratch 2>&1 | Add-Content $log
& $exe lprepare $scratch $base1 2>&1 | Add-Content $log
$vol = (& $exe lmount $scratch).Trim()
$nameGuid = (& $exe lname $base1).Trim()
Add-Content $log "vol=$vol nameGuid=$nameGuid"

# 3. schema1 JSON with networking
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee00ad"
$obj = [ordered]@{
  SystemType = "Container"; Name = "wsmprobe"; Owner = "WsmProbe"
  LayerFolderPath = $scratch
  Layers = @(@{ Id = $nameGuid; Path = $base1 })
  HvPartition = $false
  TerminateOnLastHandleClosed = $true
  NetworkEndpoints = @(@{ NetworkId = $natId; EndpointName = "wsmprobe-container" })
}
$f = "$dir\net.json"
Set-Content -Path $f -Value ($obj | ConvertTo-Json -Depth 10 -Compress) -Encoding UTF8
Add-Content $log "== json =="
Add-Content $log (Get-Content $f -Raw)

# 4. launch the container in the background (holds 300s)
Add-Content $log "== launching holder =="
$argLine = "-NoProfile -ExecutionPolicy Bypass -Command `"& '$exe' run $id '$f' 'cmd.exe /c whoami && ipconfig' 300 *> '$dir\net-run.log'`""
Start-Process powershell -Verb RunAs -ArgumentList $argLine | Out-Null
Start-Sleep -Seconds 25
Add-Content $log "== run log =="
Add-Content $log (Get-Content "$dir\net-run.log" -Raw -ErrorAction SilentlyContinue)

# 5. query IP
Add-Content $log "== hcsdiag list =="
& hcsdiag list 2>&1 | Add-Content $log
Add-Content $log "== endpoints =="
(& $exe ipquery $id 2>&1 | Select-String -Pattern 'IPAddress|MacAddress|Namespace|"ID"|State' | Select-Object -First 40) | Add-Content $log
Add-Content $log "==== done ===="
