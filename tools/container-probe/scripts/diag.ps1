$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\diag.log"
"==== diag $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$sys = "$img\Files\Windows\System32\config\SYSTEM"

L '######## image folder structure ########'
Get-ChildItem $img -Force | Select-Object Name,Length,Attributes,LinkType,Target | Format-Table -AutoSize | Out-String | Add-Content $log
L '--- Hives ---'
Get-ChildItem "$img\Hives" -Force -ErrorAction SilentlyContinue | Select-Object Name,Length,Attributes,LinkType,Target | Format-Table -AutoSize | Out-String | Add-Content $log
L '--- layout file ---'
Get-Content "$img\layout" -Raw -ErrorAction SilentlyContinue | Add-Content $log
L '--- reparse/hardlink checks ---'
L ((& fsutil.exe reparsepoint query "$img\Hives\SYSTEM" 2>&1) -join "`n")
L ((& fsutil.exe hardlink list "$sys" 2>&1) -join "`n")

L '######## offline read of image SYSTEM hive ########'
& reg.exe unload 'HKLM\WSMD' 2>&1 | Out-Null
L ('load: ' + ((& reg.exe load 'HKLM\WSMD' $sys 2>&1) -join ' '))
L ((& reg.exe query 'HKLM\WSMD\ControlSet001\Control\Session Manager' /v NumberOfInitialSessions 2>&1) -join "`n")
L ((& reg.exe query 'HKLM\WSMD\ControlSet001\Control\Session Manager' /v AutoChkTimeout 2>&1) -join "`n")

L '######## set AutoChkTimeout=0x99 marker in a copy and copy back ########'
$mod = "$dir\SYSTEM.mod2"
Copy-Item $sys $mod -Force
L ('load mod: ' + ((& reg.exe load 'HKLM\WSMM' $mod 2>&1) -join ' '))
L ('add marker: ' + ((& reg.exe add 'HKLM\WSMM\ControlSet001\Control\Session Manager' /v AutoChkTimeout /t REG_DWORD /d 0x99 /f 2>&1) -join ' '))
L ('unload mod: ' + ((& reg.exe unload 'HKLM\WSMM' 2>&1) -join ' '))
Copy-Item $mod $sys -Force
L '--- verify marker in image file ---'
& reg.exe unload 'HKLM\WSMV' 2>&1 | Out-Null
L ('load verify: ' + ((& reg.exe load 'HKLM\WSMV' $sys 2>&1) -join ' '))
L ((& reg.exe query 'HKLM\WSMV\ControlSet001\Control\Session Manager' /v AutoChkTimeout 2>&1) -join "`n")
& reg.exe unload 'HKLM\WSMV' 2>&1 | Out-Null
& reg.exe unload 'HKLM\WSMD' 2>&1 | Out-Null

L '######## start container; does it see the marker? ########'
$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d06'
$scratch = 'C:\wsmprobe\dsk6'
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '420'
& '$exe' argonnet3 $id $scratch '$img' '$natId' *> '$dir\diag-run.log'
"@
Set-Content "$dir\diag-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\diag-child.ps1" | Out-Null
Start-Sleep -Seconds 50
$runText = Get-Content "$dir\diag-run.log" -Raw -ErrorAction SilentlyContinue
L ('start ok: ' + ($runText -match 'STARTED OK'))
L ((& $exe sh $id "cmd.exe /c reg query \"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\" /v AutoChkTimeout & reg query \"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\" /v NumberOfInitialSessions & reg query \"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\SubSystems\" /v Windows" 2>&1) -join "`n")
L '######## tasklist ########'
L ((& $exe sh $id "cmd.exe /c tasklist" 2>&1) -join "`n")
L '######## done ########'
