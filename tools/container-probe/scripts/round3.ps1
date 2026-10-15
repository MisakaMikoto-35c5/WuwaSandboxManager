$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\round3.log"
"==== round3 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d08'
$scratch = 'C:\wsmprobe\dsk8'

$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '900'
& '$exe' argonnet3 $id $scratch '$img' '$natId' *> '$dir\round3-run.log'
"@
Set-Content "$dir\round3-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\round3-child.ps1" | Out-Null
Start-Sleep -Seconds 55
$runText = Get-Content "$dir\round3-run.log" -Raw -ErrorAction SilentlyContinue
L ('start ok: ' + ($runText -match 'STARTED OK'))
$mi = [regex]::Match($runText, 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
L "ip=$ip"

L '######## sessions + logon chain ########'
Set-Content "$dir\r3a.cmd" -Value 'cmd.exe /c qwinsta & echo ---- & tasklist & echo ---- & netstat -an | findstr LISTEN' -Encoding ASCII
L ((& $exe shf $id "$dir\r3a.cmd" 2>&1) -join "`n")

L '######## display / window station / input desktop ########'
$b5 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes((Get-Content "$dir\p5.ps1" -Raw)))
L ((& $exe sh $id "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -EncodedCommand $b5" 2>&1) -join "`n")

L '######## enable RDP then check listener ########'
Set-Content "$dir\r3b.cmd" -Value 'cmd.exe /c reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v fDenyTSConnections /t REG_DWORD /d 0 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v UserAuthentication /t REG_DWORD /d 0 /f & sc config TermService start= auto & net start TermService & net start UmRdpService & net start SessionEnv & echo ---- & netstat -an | findstr 3389 & qwinsta' -Encoding ASCII
L ((& $exe shf $id "$dir\r3b.cmd" 2>&1) -join "`n")

if ($ip) {
  L '######## host -> container 3389 ########'
  L ('tcp 3389: ' + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  L ((& $exe rprobe $ip 3389 2>&1) -join "`n")
  L '######## host -> container websocket rdp port 3389 after 3s ########'
  Start-Sleep -Seconds 3
  L ((& $exe rprobe $ip 3389 2>&1) -join "`n")
}
L '######## done ########'
