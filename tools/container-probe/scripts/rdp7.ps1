$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\rdp7.log"
"==== rdp7 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d10'

$inner = @'
Write-Output "--- services hosting / state ---"
Get-CimInstance Win32_Service -Filter "Name='TermService' or Name='SessionEnv' or Name='UmRdpService'" | Select-Object Name,State,ProcessId,StartMode | Format-Table -AutoSize | Out-String
$p = (Get-CimInstance Win32_Service -Filter "Name='TermService'").ProcessId
Write-Output ("TermService pid = $p")
Get-CimInstance Win32_Service | Where-Object { $_.ProcessId -eq $p } | Select-Object -ExpandProperty Name | ForEach-Object { Write-Output ("  co-hosted: " + $_) }
Write-Output "--- stop dependents, then force-restart TermService ---"
foreach ($s in 'SessionEnv','UmRdpService') { & sc.exe stop $s | Out-Null }
Start-Sleep 3
if ($p -gt 0) { & taskkill.exe /f /pid $p 2>&1 | Out-String | Write-Output }
Start-Sleep 6
foreach ($s in 'TermService','SessionEnv','UmRdpService') { Write-Output ("start " + $s + ": " + ((& net.exe start $s 2>&1) -join ' ')) }
Start-Sleep 15
Write-Output "--- probe loopback 3389 inside the container ---"
try { $c = New-Object Net.Sockets.TcpClient; $c.Connect('127.0.0.1', 3389); $c.Close(); Write-Output "loopback 127.0.0.1:3389 = OK" } catch { Write-Output ("loopback 127.0.0.1:3389 = FAIL " + $_.Exception.InnerException.Message) }
Write-Output "--- qwinsta ---"
& qwinsta 2>&1 | Out-String | Write-Output
Write-Output "--- per-process listeners via Get-NetTCPConnection ---"
try { Get-NetTCPConnection -State Listen -ErrorAction Stop | Select-Object LocalAddress,LocalPort,OwningProcess | Format-Table -AutoSize | Out-String } catch { Write-Output ("Get-NetTCPConnection: " + $_.Exception.Message) }
'@
$b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
L ((& $exe sh $id "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -EncodedCommand $b64" 2>&1) -join "`n")

L '--- host -> container :3389 ---'
$mi = [regex]::Match((( & $exe sh $id "cmd.exe /c ipconfig" 2>&1) -join "`n"), 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
L "ip=$ip"
if ($ip) {
  L ('  tcp 3389: ' + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  L ((& $exe rprobe $ip 3389 2>&1) -join "`n")
}
L '######## done ########'
