$ErrorActionPreference = 'Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\dd-start.log"
"==== start $(Get-Date -Format o) ====" | Set-Content $log

$childPath = "$dir\dd-start-child.ps1"
$child = @'
$ErrorActionPreference = 'Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$layer = 'C:\wsm-images\servercore-ltsc2022'
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0e01'
$scratch = 'C:\wsmprobe\dd1'
$out = "$dir\dd-start-child.log"
function L($t) { Add-Content $out $t }
"==== child $(Get-Date -Format o) ====" | Set-Content $out

L '--- stop leftover WsmProbe compute systems ---'
try {
  $vms = ((& hcsdiag.exe list -raw 2>&1) -join "`n") | ConvertFrom-Json
  foreach ($v in $vms) {
    L ("  {0} owner={1} state={2}" -f $v.Id, $v.Owner, $v.State)
    if ($v.Owner -eq 'WsmProbe') { (& $exe stop $v.Id 2>&1) | Out-Null }
  }
} catch { L ('hcsdiag err: ' + $_.Exception.Message) }
Start-Sleep -Seconds 4

L ('image unprepare: ' + ((& $exe lunprepare $layer 2>&1) -join ' '))
L ('image deactivate: ' + ((& $exe ldeactivate $layer 2>&1) -join ' '))

L '--- NAT network ---'
$natId = $null
try {
  $n = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  $natId = ((($n -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID
} catch {}
if (-not $natId) {
  L '  none found, creating one'
  & $exe hnsnat 2>&1 | Out-Null
  Start-Sleep -Seconds 2
  try {
    $n = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
    $natId = ((($n -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID
  } catch {}
}
L "natId=$natId"

Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$env:WSMPROBE_HOLD = '3600'
L '--- argonnet3 (hold 3600s) ---'
& $exe argonnet3 $id $scratch $layer $natId *>> $out
L '--- argonnet3 returned ---'
'@
Set-Content -Path $childPath -Value $child -Encoding UTF8

Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $childPath | Out-Null
Write-Host 'waiting ~60s for the container to come up ...'
Start-Sleep -Seconds 60

$runText = Get-Content "$dir\dd-start-child.log" -Raw -ErrorAction SilentlyContinue
$mi = [regex]::Match($runText, 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
Write-Host ("container ip = {0}" -f $ip)
@{ id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0e01'; ip = $ip; scratch = 'C:\wsmprobe\dd1' } |
    ConvertTo-Json | Set-Content "$dir\dd-state.json" -Encoding UTF8
Write-Host '==== child log (tail) ===='
Get-Content "$dir\dd-start-child.log" -ErrorAction SilentlyContinue | Select-Object -Last 45 | Add-Content $log
Get-Content $log | Select-Object -Last 45
