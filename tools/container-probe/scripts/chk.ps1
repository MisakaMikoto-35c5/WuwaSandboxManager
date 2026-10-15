$ErrorActionPreference = 'Continue'
$tmp = "$env:TEMP\opencode\wsmdiag"
$log = "$tmp\chk.log"
"==== chk $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$probe = "$tmp\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$id = '609cfaee-1162-41e6-91a4-59988cde9b63'

Add-Content $log "== hcsdiag list =="
& hcsdiag list 2>&1 | Add-Content $log

Add-Content $log "== wsm ip =="
& $exe ip $id *>&1 | Add-Content $log

Add-Content $log "== guest ipconfig =="
Add-Content $log ((& $probe sh $id "cmd.exe /c ipconfig" 2>&1) -join "`n")

Add-Content $log "== host.out =="
Add-Content $log (Get-Content "$tmp\sdbx4-host.out" -Raw -ErrorAction SilentlyContinue)
Add-Content $log "==== done ===="
