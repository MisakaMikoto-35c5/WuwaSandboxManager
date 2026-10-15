$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\rdp3.log"
"==== rdp3 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d0b'

L '######## is the container still alive? ########'
L ((& $exe sh $id "cmd.exe /c echo alive" 2>&1) -join "`n")

L '######## start a manual TCP listener on 9999 inside the container (detached) ########'
$inner = '$l=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any,9999);$l.Start();Start-Sleep 900'
$innerB64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
$outer = "Start-Process powershell -ArgumentList '-NoProfile','-EncodedCommand','$innerB64' -WindowStyle Hidden -RedirectStandardOutput 'C:\l.out' -RedirectStandardError 'C:\l.err'"
$outerB64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($outer))
$cmd = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -EncodedCommand $outerB64"
L ((& $exe sh $id $cmd 2>&1) -join "`n")

Start-Sleep -Seconds 8
L '######## inside: listeners (netstat + Get-NetTCPConnection) ########'
@'
cmd.exe /c netstat -ano & echo ---- & tasklist | findstr /i powershell & echo ---- & type C:\l.err & echo ---- & type C:\l.out
'@ | Set-Content "$dir\rdp3-a.cmd" -Encoding ASCII
L ((& $exe shf $id "$dir\rdp3-a.cmd" 2>&1) -join "`n")

L '######## host side ########'
Set-Content "$dir\rdp3-b.cmd" -Value 'cmd.exe /c ipconfig' -Encoding ASCII
$ipcfg = ((& $exe shf $id "$dir\rdp3-b.cmd" 2>&1) -join "`n")
L $ipcfg
$mi = [regex]::Match($ipcfg, 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { '192.168.248.221' }
L "ip=$ip"
L ('ping: ' + (Test-Connection -ComputerName $ip -Count 2 -Quiet -ErrorAction SilentlyContinue))
L ('tcp 9999 (manual listener): ' + (Test-NetConnection -ComputerName $ip -Port 9999 -WarningAction SilentlyContinue).TcpTestSucceeded)
L ('tcp 3389 (rdp):             ' + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)

L '######## TerminalServices operational logs ########'
Set-Content "$dir\rdp3-c.cmd" -Value 'cmd.exe /c wevtutil gl "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational" & echo ==== & wevtutil qe "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational" /c:15 /rd:true /f:text & echo ==== & wevtutil qe "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational" /c:15 /rd:true /f:text' -Encoding ASCII
L ((& $exe shf $id "$dir\rdp3-c.cmd" 2>&1) -join "`n")

L '######## Transport/driver state ########'
@'
cmd.exe /c sc query termdd & sc query UmRdpService & sc query SessionEnv & sc query RDPDR & sc query RdpVideoMiniport & dir C:\Windows\System32\tdtcp.dll C:\Windows\System32\tssecsrv.dll C:\Windows\System32\rdpcorets.dll C:\Windows\System32\termsrv.dll C:\Windows\System32\drivers\tdtcp.sys
'@ | Set-Content "$dir\rdp3-d.cmd" -Encoding ASCII
L ((& $exe shf $id "$dir\rdp3-d.cmd" 2>&1) -join "`n")
L '######## done ########'
