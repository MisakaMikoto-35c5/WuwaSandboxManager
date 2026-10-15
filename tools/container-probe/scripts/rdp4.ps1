$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\rdp4.log"
"==== rdp4 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d0b'

L '--- kill stale rdp3/hcsprobe processes ---'
Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -match 'rdp3\.ps1' -or $_.Name -eq 'hcsprobe.exe' } | ForEach-Object {
  L ("  kill {0} {1}" -f $_.ProcessId, $_.Name)
  Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 3

L '--- container alive? ---'
L ((& $exe sh $id "cmd.exe /c echo alive & ipconfig" 2>&1) -join "`n")

L '--- get container IP ---'
Set-Content "$dir\r4b.cmd" -Value 'cmd.exe /c ipconfig' -Encoding ASCII
$ipcfg = ((& $exe shf $id "$dir\r4b.cmd" 2>&1) -join "`n")
$mi = [regex]::Match($ipcfg, 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
L "ip=$ip"

L '--- start listener on 9999 inside the container (background job holds the exec) ---'
$inner = '$l=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any,9999);$l.Start();Write-Output ("listening " + $l.LocalEndpoint);Start-Sleep 120'
$b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
$cmd = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -EncodedCommand $b64"
$job = Start-Job -ScriptBlock { param($exe,$id,$cmd) & $exe sh $id $cmd } -ArgumentList $exe, $id, $cmd
Start-Sleep -Seconds 18

L '--- host -> container probes ---'
if ($ip) {
  L ('  ping:      ' + (Test-Connection -ComputerName $ip -Count 2 -Quiet -ErrorAction SilentlyContinue))
  L ('  tcp 9999:  ' + (Test-NetConnection -ComputerName $ip -Port 9999 -WarningAction SilentlyContinue).TcpTestSucceeded)
  L ('  tcp 3389:  ' + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  L ('  rprobe 9999:')
  L ((& $exe rprobe $ip 9999 2>&1) -join "`n")
}

L '--- netstat inside while the listener is up ---'
L ((& $exe sh $id "cmd.exe /c netstat -an" 2>&1) -join "`n")

L '--- job output ---'
L ((Receive-Job $job 2>&1) -join "`n")
Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue

L '--- TerminalServices operational logs ---'
@'
cmd.exe /c wevtutil gl "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational" & echo ==== & wevtutil qe "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational" /c:12 /rd:true /f:text & echo ==== & wevtutil qe "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational" /c:12 /rd:true /f:text
'@ | Set-Content "$dir\r4c.cmd" -Encoding ASCII
L ((& $exe shf $id "$dir\r4c.cmd" 2>&1) -join "`n")

L '--- drivers / files ---'
@'
cmd.exe /c sc query termdd & sc query RDPDR & sc query RdpVideoMiniport & echo ==== & dir C:\Windows\System32\tdtcp.dll C:\Windows\System32\tssecsrv.dll C:\Windows\System32\termsrv.dll C:\Windows\System32\drivers\tdtcp.sys C:\Windows\System32\rdpbase.dll
'@ | Set-Content "$dir\r4d.cmd" -Encoding ASCII
L ((& $exe shf $id "$dir\r4d.cmd" 2>&1) -join "`n")
L '######## done ########'
