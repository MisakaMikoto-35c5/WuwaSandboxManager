$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\ws22net4.log"
"==== ws22net4 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base1 = 'C:\wsmprobe\img\base1'

# create NAT network if missing
$list = & $exe hns GET "/networks/" "" 2>&1
Add-Content $log "== networks =="
Add-Content $log ($list -join "`n")
$natId = $null
try {
  $env = ($list -join "`n") | ConvertFrom-Json
  $out = $env.Output | ConvertFrom-Json
  $nat = $out | Where-Object { $_.Type -eq 'NAT' -and $_.Name -eq 'wsm-nat' } | Select-Object -First 1
  if ($nat) { $natId = $nat.ID }
} catch { Add-Content $log "parse err: $($_.Exception.Message)" }

if (-not $natId) {
  $body = '{"Name":"wsm-nat","Type":"NAT","Subnets":[{"AddressPrefix":"192.168.248.0/24","GatewayAddress":"192.168.248.1"}]}'
  Add-Content $log "== creating NAT network =="
  $created = & $exe hns POST "/networks/" $body 2>&1
  Add-Content $log ($created -join "`n")
  try {
    $env2 = ($created -join "`n") | ConvertFrom-Json
    $o2 = $env2.Output | ConvertFrom-Json
    $natId = $o2.ID
  } catch { Add-Content $log "create parse err: $($_.Exception.Message)" }
}
Add-Content $log "natId=$natId"
if (-not $natId) { Add-Content $log "abort"; Add-Content $log "==== done ===="; exit }

$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee00b0"
$scratch = "C:\wsmprobe\ws22net4"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe argonnet $id $scratch $base1 $natId "cmd.exe /c whoami && ipconfig" 0 2>&1 | Add-Content $log
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
