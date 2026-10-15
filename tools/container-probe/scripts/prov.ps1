$ErrorActionPreference = 'SilentlyContinue'
$log = "$env:TEMP\opencode\wsmdiag\prov.log"
"==== providers/logs ====" | Set-Content $log
Get-WinEvent -ListLog * -ErrorAction SilentlyContinue |
  Where-Object { $_.LogName -match 'Wcifs|Compute|Container|Hcs|Silo|Hyper-V' } |
  Select-Object LogName, RecordCount, IsEnabled | Format-Table -AutoSize | Out-String | Add-Content $log
Add-Content $log "==== providers ===="
(logman query providers 2>&1) | Select-String -Pattern 'Compute|Wcifs|Container|Hcs|vmcompute|Sandbox' | Add-Content $log
Add-Content $log "==== done ===="
