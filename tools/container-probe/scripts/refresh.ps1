$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\refresh.log"
"==== refresh test $(Get-Date -Format o) ====" | Set-Content $log
function DumpLayers([string]$tag) {
  Add-Content $log "---- layers $tag ----"
  $k = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Containers\CmService\Layers'
  Get-ChildItem $k -ErrorAction SilentlyContinue | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath
    "{0} seq={1} os={2}" -f $_.PSChildName, $p.SequenceNumber, $p.OSVersion | Add-Content $log
  }
}
DumpLayers "before"
Add-Content $log "== starting WindowsSandbox.exe =="
$p = Start-Process 'C:\Windows\System32\WindowsSandbox.exe' -PassThru
Add-Content $log "pid=$($p.Id)"
for ($i = 1; $i -le 8; $i++) {
  Start-Sleep -Seconds 15
  Add-Content $log "-- t=$($i*15)s processes:"
  Get-Process -Name 'WindowsSandbox*','vmmem*','vmwp','CmService*','cmimageworker' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessName -Unique | Add-Content $log
}
Add-Content $log "== compute systems =="
hcsdiag list 2>&1 | Add-Content $log
DumpLayers "after"
Add-Content $log "== stopping =="
Get-Process 'WindowsSandbox*' -ErrorAction SilentlyContinue | Stop-Process -Force
hcsdiag terminate 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
