$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\rdp5.log"
"==== rdp5 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d0c'
$scratch = 'C:\wsmprobe\dskC'

$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '5400'
& '$exe' argonnet3 $id $scratch '$img' '$natId' *> '$dir\rdp5-run.log'
"@
Set-Content "$dir\rdp5-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\rdp5-child.ps1" | Out-Null
Start-Sleep -Seconds 60
$runText = Get-Content "$dir\rdp5-run.log" -Raw -ErrorAction SilentlyContinue
L ('start ok: ' + ($runText -match 'STARTED OK'))
$mi = [regex]::Match($runText, 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
L "ip=$ip"

L '--- baseline ---'
L ((& $exe sh $id "cmd.exe /c qwinsta & sc query TermService & netstat -an" 2>&1) -join "`n")

L '--- manual listener 9999 (background job) ---'
$inner = '$l=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Any,9999);$l.Start();Write-Output ("listening " + $l.LocalEndpoint);Start-Sleep 150'
$b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($inner))
$cmd = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -EncodedCommand $b64"
$job = Start-Job -ScriptBlock { param($exe,$id,$cmd) & $exe sh $id $cmd } -ArgumentList $exe, $id, $cmd
Start-Sleep -Seconds 20

L '--- host -> container ---'
if ($ip) {
  L ('  ping:     ' + (Test-Connection -ComputerName $ip -Count 2 -Quiet -ErrorAction SilentlyContinue))
  L ('  tcp 9999: ' + (Test-NetConnection -ComputerName $ip -Port 9999 -WarningAction SilentlyContinue).TcpTestSucceeded)
  L ('  tcp 3389: ' + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
}
L '--- netstat inside (listener up) ---'
L ((& $exe sh $id "cmd.exe /c netstat -an" 2>&1) -join "`n")
L '--- job output ---'
L ((Receive-Job $job 2>&1) -join "`n")
Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue

L '--- TerminalServices operational logs ---'
@'
cmd.exe /c wevtutil gl "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational" & echo ==== & wevtutil qe "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational" /c:12 /rd:true /f:text & echo ==== & wevtutil qe "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational" /c:12 /rd:true /f:text
'@ | Set-Content "$dir\r5c.cmd" -Encoding ASCII
L ((& $exe shf $id "$dir\r5c.cmd" 2>&1) -join "`n")

L '--- drivers / files ---'
@'
cmd.exe /c sc query termdd & sc query RDPDR & sc query RdpVideoMiniport & echo ==== & dir C:\Windows\System32\tdtcp.dll C:\Windows\System32\tssecsrv.dll C:\Windows\System32\termsrv.dll C:\Windows\System32\drivers\tdtcp.sys
'@ | Set-Content "$dir\r5d.cmd" -Encoding ASCII
L ((& $exe shf $id "$dir\r5d.cmd" 2>&1) -join "`n")
L '######## done ########'
