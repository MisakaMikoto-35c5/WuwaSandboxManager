$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\clean2.log"
"==== clean2 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$sys = "$img\Files\Windows\System32\config\SYSTEM"
$sof = "$img\Files\Windows\System32\config\SOFTWARE"

L '--- stop WsmProbe containers ---'
try { $vms = ((& hcsdiag.exe list -raw 2>&1) -join "`n") | ConvertFrom-Json
  foreach ($v in $vms) { L ("  {0} owner={1} state={2}" -f $v.Id,$v.Owner,$v.State); if ($v.Owner -eq 'WsmProbe') { (& $exe stop $v.Id 2>&1) | Out-Null } } } catch { L ('err ' + $_.Exception.Message) }
Start-Sleep -Seconds 4
L ('unprepare: ' + ((& $exe lunprepare $img 2>&1) -join ' '))
L ('deactivate: ' + ((& $exe ldeactivate $img 2>&1) -join ' '))
Start-Sleep -Seconds 2

L '--- restore pristine hives, then re-apply ONLY NumberOfInitialSessions=2 ---'
Copy-Item "$dir\SYSTEM.orig" $sys -Force -ErrorAction SilentlyContinue
Copy-Item "$dir\SOFT.orig" $sof -Force -ErrorAction SilentlyContinue
L ('SYSTEM restored hash: ' + (Get-FileHash $sys -Algorithm SHA256).Hash.Substring(0,16))
L ('SOFTWARE restored hash: ' + (Get-FileHash $sof -Algorithm SHA256).Hash.Substring(0,16))
Copy-Item $sys "$dir\SYSTEM.final" -Force -ErrorAction SilentlyContinue
& reg.exe unload 'HKLM\WCL' 2>&1 | Out-Null
L ('load: ' + ((& reg.exe load 'HKLM\WCL' "$dir\SYSTEM.final" 2>&1) -join ' '))
foreach ($cs in 'ControlSet001','ControlSet002') {
  L ("  " + $cs + ': ' + ((& reg.exe add "HKLM\WCL\$cs\Control\Session Manager" /v NumberOfInitialSessions /t REG_DWORD /d 2 /f 2>&1) -join ' '))
}
L ('unload: ' + ((& reg.exe unload 'HKLM\WCL' 2>&1) -join ' '))
Copy-Item "$dir\SYSTEM.final" $sys -Force -ErrorAction SilentlyContinue

L '--- final verification ---'
& reg.exe unload 'HKLM\WV' 2>&1 | Out-Null
Copy-Item $sys "$dir\Wv2" -Force -ErrorAction SilentlyContinue
& reg.exe load 'HKLM\WV' "$dir\Wv2" 2>&1 | Out-Null
L ((& reg.exe query 'HKLM\WV\ControlSet001\Control\Session Manager' /v NumberOfInitialSessions 2>&1) -join "`n")
L '--- RDP experiment knobs gone? ---'
foreach ($q in @(
  'HKLM\WV\ControlSet001\Control\Terminal Server','HKLM\WV\ControlSet001\Control\Terminal Server\Wds',
  'HKLM\WV\ControlSet001\Services\TermService','HKLM\WV\ControlSet001\Control\Session Manager\Environment')) {
  L ((& reg.exe query $q 2>&1 | Select-String -Pattern 'AllowListeners|ContainerMode|Start |fDenyTS' | Out-String).Trim())
}
& reg.exe unload 'HKLM\WV' 2>&1 | Out-Null
L ('image SYSTEM hash: ' + (Get-FileHash $sys -Algorithm SHA256).Hash.Substring(0,16))
L ('image SOFTWARE hash: ' + (Get-FileHash $sof -Algorithm SHA256).Hash.Substring(0,16))
L '--- leftovers ---'
Get-ChildItem 'C:\wsmprobe' -Directory -ErrorAction SilentlyContinue | Measure-Object | ForEach-Object { L ("wsmprobe scratch dirs: " + $_.Count) }
L '######## done ########'
