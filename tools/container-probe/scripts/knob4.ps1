$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\knob4.log"
"==== knob4 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$sys = "$img\Files\Windows\System32\config\SYSTEM"

L '--- stop containers + release layer ---'
try { $vms = ((& hcsdiag.exe list -raw 2>&1) -join "`n") | ConvertFrom-Json
  foreach ($v in $vms) { if ($v.Owner -eq 'WsmProbe') { L ("  stop " + $v.Id); (& $exe stop $v.Id 2>&1) | Out-Null } } } catch { L ('err ' + $_.Exception.Message) }
Start-Sleep -Seconds 4
L ('deactivate: ' + ((& $exe ldeactivate $img 2>&1) -join ' '))
Start-Sleep -Seconds 2

L '--- patch SYSTEM: machine env var + Wds value ---'
Copy-Item $sys "$dir\SYSTEM.knob4" -Force -ErrorAction SilentlyContinue
& reg.exe unload 'HKLM\WSK4' 2>&1 | Out-Null
L ('load: ' + ((& reg.exe load 'HKLM\WSK4' "$dir\SYSTEM.knob4" 2>&1) -join ' '))
foreach ($cs in 'ControlSet001','ControlSet002') {
  L ("  env: " + ((& reg.exe add "HKLM\WSK4\$cs\Control\Session Manager\Environment" /v AllowListenersInsideArgonContainer /t REG_SZ /d 1 /f 2>&1) -join ' '))
  L ("  wds: " + ((& reg.exe add "HKLM\WSK4\$cs\Control\Terminal Server\Wds" /v AllowListenersInsideArgonContainer /t REG_DWORD /d 1 /f 2>&1) -join ' '))
}
L ('unload: ' + ((& reg.exe unload 'HKLM\WSK4' 2>&1) -join ' '))
Copy-Item "$dir\SYSTEM.knob4" $sys -Force -ErrorAction SilentlyContinue

L '--- start container ---'
$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d10'
$scratch = 'C:\wsmprobe\dskG'
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '7200'
& '$exe' argonnet3 $id $scratch '$img' '$natId' *> '$dir\knob4-run.log'
"@
Set-Content "$dir\knob4-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\knob4-child.ps1" | Out-Null
Start-Sleep -Seconds 60
$runText = Get-Content "$dir\knob4-run.log" -Raw -ErrorAction SilentlyContinue
L ('start ok: ' + ($runText -match 'STARTED OK'))
$mi = [regex]::Match($runText, 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
L "ip=$ip"

L '--- inside: env var visible? qwinsta? ---'
@'
cmd.exe /c set AllowListenersInsideArgonContainer & echo ---- & qwinsta & echo ---- & sc queryex UmRdpService
'@ | Set-Content "$dir\k4-a.cmd" -Encoding ASCII
L ((& $exe shf $id "$dir\k4-a.cmd" 2>&1) -join "`n")

if ($ip) {
  L '--- host -> container :3389 ---'
  L ('  tcp 3389: ' + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
}

L '--- SCM service-failure events (7000/7001/7024) ---'
Set-Content "$dir\k4-b.cmd" -Value 'cmd.exe /c wevtutil qe System "/q:*[System[(EventID=7000 or EventID=7001 or EventID=7024 or EventID=7031 or EventID=7034)]]" /c:12 /rd:true /f:text' -Encoding ASCII
L ((& $exe shf $id "$dir\k4-b.cmd" 2>&1) -join "`n")

L '--- RCM / LSM operational logs (40) ---'
@'
cmd.exe /c wevtutil qe "Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational" /c:40 /rd:true /f:text & echo ==== & wevtutil qe "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational" /c:40 /rd:true /f:text
'@ | Set-Content "$dir\k4-c.cmd" -Encoding ASCII
L ((& $exe shf $id "$dir\k4-c.cmd" 2>&1) -join "`n")

L '--- System log entries mentioning RDP/Terminal/Listener ---'
Set-Content "$dir\k4-d.cmd" -Value 'cmd.exe /c wevtutil qe System /c:120 /rd:true /f:text' -Encoding ASCII
$syslog = ((& $exe shf $id "$dir\k4-d.cmd" 2>&1) -join "`n")
L (($syslog -split "`n" | Where-Object { $_ -match 'erminal|rdp|RDP|Listene|listen|Remote' }) -join "`n")
L '######## done ########'
