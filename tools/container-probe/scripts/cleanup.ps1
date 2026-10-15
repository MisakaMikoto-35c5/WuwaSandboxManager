$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\cleanup.log"
"==== cleanup $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$sys = "$img\Files\Windows\System32\config\SYSTEM"

L '--- containers before ---'
try {
  $vms = ((& hcsdiag.exe list -raw 2>&1) -join "`n") | ConvertFrom-Json
  foreach ($v in $vms) {
    L ("  {0} owner={1} state={2} type={3}" -f $v.Id, $v.Owner, $v.State, $v.SystemType)
    if ($v.Owner -eq 'WsmProbe') { (& $exe stop $v.Id 2>&1) | Out-Null; L "    stopped" }
  }
} catch { L ('err ' + $_.Exception.Message) }
Start-Sleep -Seconds 4

L '--- containers after ---'
try {
  $vms = ((& hcsdiag.exe list -raw 2>&1) -join "`n") | ConvertFrom-Json
  foreach ($v in $vms) { L ("  {0} owner={1} state={2} type={3}" -f $v.Id, $v.Owner, $v.State, $v.SystemType) }
} catch { L ('err ' + $_.Exception.Message) }

L '--- image final state ---'
L ('wsm-image.json: ' + ((Get-Content "$img\wsm-image.json" -Raw -ErrorAction SilentlyContinue) -replace "`r?`n",' '))
L ('SYSTEM hash: ' + (Get-FileHash $sys -Algorithm SHA256).Hash.Substring(0,16))
L ('pristine backup SYSTEM.orig hash: ' + (Get-FileHash "$dir\SYSTEM.orig" -Algorithm SHA256).Hash.Substring(0,16))
& reg.exe unload 'HKLM\WCHK' 2>&1 | Out-Null
Copy-Item $sys "$dir\Wchk" -Force
& reg.exe load 'HKLM\WCHK' "$dir\Wchk" 2>&1 | Out-Null
L ((& reg.exe query 'HKLM\WCHK\ControlSet001\Control\Session Manager' /v NumberOfInitialSessions 2>&1) -join "`n")
L ((& reg.exe query 'HKLM\WCHK\ControlSet002\Control\Session Manager' /v NumberOfInitialSessions 2>&1) -join "`n")
& reg.exe unload 'HKLM\WCHK' 2>&1 | Out-Null
L '--- leftovers ---'
Get-ChildItem 'C:\wsmprobe' -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | Add-Content $log
L '--- done ---'
