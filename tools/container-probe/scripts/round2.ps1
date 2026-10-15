$ErrorActionPreference = 'Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\round2.log"
"==== round2 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$layer = 'C:\wsm-images\servercore-ltsc2022'
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d04'
$scratch = 'C:\wsmprobe\dsk4'
function Log($t) { Add-Content $log $t }

# ---------- offline hive check ----------
Log '######## OFFLINE: image SYSTEM hive ########'
& reg.exe load 'HKLM\WSMIMG' "$layer\Hives\SYSTEM" 2>&1 | Add-Content $log
Log '--- ControlSet001\Control\Session Manager (image) ---'
(& reg.exe query 'HKLM\WSMIMG\ControlSet001\Control\Session Manager' 2>&1) | Add-Content $log
Log '--- SubSystems (image) ---'
(& reg.exe query 'HKLM\WSMIMG\ControlSet001\Control\Session Manager\SubSystems' /s 2>&1) | Add-Content $log
Log '--- ProductOptions (image) ---'
(& reg.exe query 'HKLM\WSMIMG\ControlSet001\Control\ProductOptions' 2>&1) | Add-Content $log
& reg.exe unload 'HKLM\WSMIMG' 2>&1 | Add-Content $log
Log '######## HOST contrast ########'
Log '--- ControlSet001\Control\Session Manager (host) ---'
(& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Control\Session Manager' 2>&1) | Add-Content $log
Log '--- SubSystems (host) ---'
(& reg.exe query 'HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\SubSystems' /s 2>&1) | Add-Content $log

# ---------- start a live container ----------
$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
Log "natId=$natId"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '900'
& '$exe' argonnet3 $id $scratch '$layer' '$natId' *> '$dir\round2-run.log'
"@
Set-Content "$dir\round2-child.ps1" -Value $child -Encoding UTF8
Log 'starting container...'
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\round2-child.ps1" | Out-Null
Start-Sleep -Seconds 50
$runText = Get-Content "$dir\round2-run.log" -Raw -ErrorAction SilentlyContinue
Log ("start ok: " + ($runText -match 'STARTED OK'))
Log '######## LIVE: reg/file probes ########'
Log ((& $exe shf $id "$dir\round2.cmd" 2>&1) -join "`n")
Log '######## LIVE: window station + display metrics ########'
$b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes((Get-Content "$dir\p5.ps1" -Raw)))
Log ((& $exe sh $id "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -EncodedCommand $b64" 2>&1) -join "`n")
Log '######## LIVE: process list ########'
Log ((& $exe sh $id "cmd.exe /c tasklist" 2>&1) -join "`n")
Log "==== done ===="
