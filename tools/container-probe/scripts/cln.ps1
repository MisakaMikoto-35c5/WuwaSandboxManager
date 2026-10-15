$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\cln.log"
"==== cln $(Get-Date -Format o) ====" | Set-Content $log
$probe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$layer = 'C:\wsm-images\server-ltsc2022'

Add-Content $log "== UVM folder of the Desktop-Experience image =="
if (Test-Path "$layer\UtilityVM") {
  Get-ChildItem "$layer\UtilityVM" -Recurse -Depth 2 -ErrorAction SilentlyContinue | Select-Object -First 20 -ExpandProperty FullName | Add-Content $log
  Add-Content $log ("UVM explorer.exe: " + (Test-Path "$layer\UtilityVM\Files\Windows\explorer.exe"))
  Add-Content $log ("UVM cmd.exe: " + (Test-Path "$layer\UtilityVM\Files\Windows\System32\cmd.exe"))
  Add-Content $log ("UVM termsrv.dll: " + (Test-Path "$layer\UtilityVM\Files\Windows\System32\termsrv.dll"))
} else { Add-Content $log "no UtilityVM folder" }

Add-Content $log "== leftover VMs =="
$list = (& hcsdiag list -raw 2>&1) -join "`n"
try {
  foreach ($vm in ($list | ConvertFrom-Json)) {
    if ($vm.Owner -eq 'WindowsSandbox' -or $vm.SystemType -eq 'Container') {
      Add-Content $log "stopping $($vm.Id) ($($vm.SystemType))"
      & $probe stop $vm.Id 2>&1 | Add-Content $log
    }
  }
} catch { Add-Content $log "parse err: $($_.Exception.Message)" }

Add-Content $log "== disk before cleanup =="
Get-PSDrive C | Select-Object @{n='FreeGB';e={[math]::Round($_.Free/1GB,1)}} | Out-String | Add-Content $log

Add-Content $log "== removing the Desktop-Experience test image (~12 GB) =="
Remove-Item -Recurse -Force $layer -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force 'C:\wsm-images\server-ltsc2022.src' -ErrorAction SilentlyContinue
Add-Content $log "removed"

Add-Content $log "== disk after cleanup =="
Get-PSDrive C | Select-Object @{n='FreeGB';e={[math]::Round($_.Free/1GB,1)}} | Out-String | Add-Content $log
Add-Content $log "== kept images =="
Get-ChildItem 'C:\wsm-images' -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | Add-Content $log
Get-ChildItem 'C:\wsmprobe\img' -Directory -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name | Add-Content $log
Add-Content $log "==== done ===="
