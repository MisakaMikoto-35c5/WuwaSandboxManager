$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\final.log"
"==== final $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$sys = "$img\Files\Windows\System32\config\SYSTEM"

L '######## stop everything ########'
try {
  $raw = (& hcsdiag.exe list -raw 2>&1) -join "`n"
  $vms = $raw | ConvertFrom-Json
  foreach ($v in $vms) { L ("  vm: {0} owner={1} state={2} type={3}" -f $v.Id, $v.Owner, $v.State, $v.SystemType); if ($v.SystemType -eq 'Container') { (& $exe stop $v.Id 2>&1) | ForEach-Object { L ("    " + $_) } } }
} catch { L ('hcsdiag err: ' + $_.Exception.Message) }
Start-Sleep -Seconds 4
(& $exe lunprepare $img 2>&1) | ForEach-Object { L ("unprepare: " + $_) }
(& $exe ldeactivate $img 2>&1) | ForEach-Object { L ("deactivate: " + $_) }
Start-Sleep -Seconds 2

L '######## file hashes ########'
foreach ($f in $sys, "$dir\SYSTEM.orig", "$dir\SYSTEM.mod") {
  $i = Get-Item $f -ErrorAction SilentlyContinue
  if ($i) { L ("{0}  {1:N0}  {2}" -f $f, $i.Length, (Get-FileHash $f -Algorithm SHA256).Hash.Substring(0,16)) } else { L "$f (missing)" }
}

L '######## read current image hive ########'
$c1 = "$dir\Wcur"
Remove-Item $c1 -ErrorAction SilentlyContinue
Copy-Item $sys $c1 -Force -ErrorAction SilentlyContinue
L ('copy of image hive ok: ' + (Test-Path $c1) + '  size=' + ((Get-Item $c1 -ErrorAction SilentlyContinue).Length))
& reg.exe unload 'HKLM\W1' 2>&1 | Out-Null
L ('load: ' + ((& reg.exe load 'HKLM\W1' $c1 2>&1) -join ' '))
L '--- ControlSet001 Session Manager (selected values) ---'
L ((& reg.exe query 'HKLM\W1\ControlSet001\Control\Session Manager' 2>&1) -join "`n")
L '--- ControlSet002 Session Manager (selected values) ---'
L ((& reg.exe query 'HKLM\W1\ControlSet002\Control\Session Manager' 2>&1) -join "`n")
& reg.exe unload 'HKLM\W1' 2>&1 | Out-Null

L '######## patch copy: NumberOfInitialSessions=2 in both control sets ########'
$c2 = "$dir\Wpatch"
Copy-Item $sys $c2 -Force -ErrorAction SilentlyContinue
& reg.exe unload 'HKLM\W2' 2>&1 | Out-Null
L ('load: ' + ((& reg.exe load 'HKLM\W2' $c2 2>&1) -join ' '))
foreach ($cs in 'ControlSet001','ControlSet002') {
  L ('add ' + $cs + ': ' + ((& reg.exe add "HKLM\W2\$cs\Control\Session Manager" /v NumberOfInitialSessions /t REG_DWORD /d 2 /f 2>&1) -join ' '))
}
L ('unload: ' + ((& reg.exe unload 'HKLM\W2' 2>&1) -join ' '))
L '--- write patched hive back over the image file ---'
$werr = $null
Copy-Item $c2 $sys -Force -ErrorAction SilentlyContinue -ErrorVariable werr
L ('copy error: ' + ($werr | Out-String))
L ('image hive now: size=' + ((Get-Item $sys -ErrorAction SilentlyContinue).Length) + '  hash=' + (Get-FileHash $sys -Algorithm SHA256).Hash.Substring(0,16))
& reg.exe unload 'HKLM\W3' 2>&1 | Out-Null
Copy-Item $sys "$dir\Wv" -Force -ErrorAction SilentlyContinue
L ('verify load: ' + ((& reg.exe load 'HKLM\W3' "$dir\Wv" 2>&1) -join ' '))
L ((& reg.exe query 'HKLM\W3\ControlSet001\Control\Session Manager' /v NumberOfInitialSessions 2>&1) -join "`n")
& reg.exe unload 'HKLM\W3' 2>&1 | Out-Null

L '######## start container on patched image ########'
$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d07'
$scratch = 'C:\wsmprobe\dsk7'
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '600'
& '$exe' argonnet3 $id $scratch '$img' '$natId' *> '$dir\final-run.log'
"@
Set-Content "$dir\final-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\final-child.ps1" | Out-Null
Start-Sleep -Seconds 55
$runText = Get-Content "$dir\final-run.log" -Raw -ErrorAction SilentlyContinue
L ('start ok: ' + ($runText -match 'STARTED OK'))
L '--- Session Manager inside the patched container ---'
Set-Content "$dir\final-in.cmd" -Value 'cmd.exe /c reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager"' -Encoding ASCII
L ((& $exe shf $id "$dir\final-in.cmd" 2>&1) -join "`n")
L '--- qwinsta ---'
Set-Content "$dir\final-q.cmd" -Value 'cmd.exe /c qwinsta & query session & query user & echo --- & tasklist' -Encoding ASCII
L ((& $exe shf $id "$dir\final-q.cmd" 2>&1) -join "`n")
L '######## done ########'
