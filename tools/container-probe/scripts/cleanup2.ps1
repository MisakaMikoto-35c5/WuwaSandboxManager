$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\cleanup2.log"
"==== cleanup2 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"

# leftover namespaces from the probe experiments
$namespaces = @(
  'FABCFFC4-6D09-4152-B03E-A6A7A789BA6B','FD07BEE6-6E35-4EBA-8226-20EC647B8090',
  '910F7D92-BA2D-4C3F-98AE-7C0AC590D2DC','25A37F7C-0198-404A-A946-866E6D9A06DD',
  '35E9480F-6D46-4FC8-8D3C-B14CB1345E01','4896A675-6456-45DB-9D24-8A665519E6F8',
  '7F348C79-F9CE-4BBD-BB35-E9B17FD5F706','ADE92135-B94D-4510-88F5-305F07CE3056',
  'DBBD8B6A-EF84-4688-9AED-9A7ACA569DE9','9B670D00-38ED-4204-8DB0-A3D44207B8C5'
)
foreach ($ns in $namespaces) {
  Add-Content $log "delete namespace $ns : $(( & $exe hns DELETE "/namespaces/$ns" '' 2>&1 ) -join ' ')"
}

# leftover endpoints named wsm-container-*/wsmprobe-*
$out = (& $exe hns GET "/endpoints/" "" 2>&1) -join "`n"
try {
  $env = $out | ConvertFrom-Json
  foreach ($ep in $env.Output) {
    if ($ep.Name -match '^wsm-container-|^wsmprobe-') {
      Add-Content $log "delete endpoint $($ep.Name) : $(( & $exe hns DELETE "/endpoints/$($ep.ID)" '' 2>&1 ) -join ' ')"
    }
  }
} catch { Add-Content $log "endpoint parse err: $($_.Exception.Message)" }

Add-Content $log "== remaining endpoints named wsm =="
((& $exe hns GET "/endpoints/" "" 2>&1) -join "`n") | Select-String 'wsm-container|wsmprobe' | Add-Content $log
Add-Content $log "==== done ===="
