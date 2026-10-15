$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\rdp6.log"
"==== rdp6 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d10'

L '--- container alive? ---'
L ((& $exe sh $id "cmd.exe /c echo alive & ipconfig & qwinsta" 2>&1) -join "`n")

$inner = @'
function T($h,$p){ $c=New-Object Net.Sockets.TcpClient; try{ $c.Connect($h,$p); "OK $h`:$p"; $c.Close() } catch { "FAIL $h`:$p -> " + $_.Exception.InnerException.Message } }
T "127.0.0.1" 3389
T "127.0.0.1" 9999
T (([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object { $_.OperationalStatus -eq 'Up' -and $_.GetIPProperties().UnicastAddresses.AddressFamily -eq 'InterNetwork' } | ForEach-Object { $_.GetIPProperties().UnicastAddresses } | Where-Object { $_.Address.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1).Address.IPAddressToString) 3389
Get-Process -Name svchost | Select-Object Id,@{n='cmd';e={(Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)").CommandLine}} | Format-Table -AutoSize | Out-String -Width 200
'@
$b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
L '--- inside the container: loopback / self-IP connect to 3389, and svchost command lines ---'
L ((& $exe sh $id "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -EncodedCommand $b64" 2>&1) -join "`n")

L '--- host: who owns :3389 ---'
$ns = (netstat -ano | Select-String ':3389')
L ($ns | Out-String)
foreach ($line in $ns) {
  $parts = ($line.ToString() -split '\s+') | Where-Object { $_ }
  $pid = $parts[-1]
  if ($pid -match '^\d+$') { L ("  PID $pid = " + ((Get-Process -Id $pid -ErrorAction SilentlyContinue).ProcessName)) }
}
L '--- host: container IP 3389 ---'
$mi = [regex]::Match((( & $exe sh $id "cmd.exe /c ipconfig" 2>&1) -join "`n"), 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
L "ip=$ip"
if ($ip) { L ('  tcp 3389: ' + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded) }
L '######## done ########'
