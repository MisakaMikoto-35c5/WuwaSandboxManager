$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\ws22net5.log"
"==== ws22net5 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base1 = 'C:\wsmprobe\img\base1'

$natId = $null
$existing = Get-HnsNetwork | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1
if ($existing) { $natId = $existing.ID; Add-Content $log "existing NAT: $($existing.Name) $natId" }
else {
  Add-Content $log "== creating wsm-nat =="
  $created = & $exe hnsnat 2>&1
  Add-Content $log ($created -join "`n")
  $natId = (Get-HnsNetwork | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1).ID
}
Add-Content $log "natId=$natId"
if (-not $natId) { Add-Content $log "abort"; Add-Content $log "==== done ===="; exit }

$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee00b1"
$scratch = "C:\wsmprobe\ws22net5"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe argonnet $id $scratch $base1 $natId "cmd.exe /c whoami && ipconfig" 0 2>&1 | Add-Content $log
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
