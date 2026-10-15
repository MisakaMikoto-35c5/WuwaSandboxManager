$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\ws22net3.log"
"==== ws22net3 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base1 = 'C:\wsmprobe\img\base1'

# ensure a NAT network
$nat = Get-HnsNetwork | Where-Object { $_.Type -eq 'NAT' -and $_.Name -eq 'wsm-nat' } | Select-Object -First 1
if (-not $nat) {
  Add-Content $log "creating NAT network wsm-nat"
  try {
    $nat = New-HnsNetwork -Type NAT -Name 'wsm-nat' -AddressPrefix '192.168.248.0/24' -Gateway '192.168.248.1' -Verbose
  } catch { Add-Content $log "New-HnsNetwork err: $($_.Exception.Message)" }
}
if (-not $nat) { Add-Content $log "no NAT network; aborting"; Add-Content $log "==== done ===="; exit }
$natId = $nat.ID
Add-Content $log "NAT id=$natId"

$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee00af"
$scratch = "C:\wsmprobe\ws22net3"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe argonnet $id $scratch $base1 $natId "cmd.exe /c whoami && ipconfig /all" 0 2>&1 | Add-Content $log
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
