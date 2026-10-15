$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\rdp1.log"
"==== rdp1 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d0a'
$scratch = 'C:\wsmprobe\dskA'

$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '1800'
& '$exe' argonnet3 $id $scratch '$img' '$natId' *> '$dir\rdp1-run.log'
"@
Set-Content "$dir\rdp1-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\rdp1-child.ps1" | Out-Null
Start-Sleep -Seconds 55
$runText = Get-Content "$dir\rdp1-run.log" -Raw -ErrorAction SilentlyContinue
L ('start ok: ' + ($runText -match 'STARTED OK'))
$mi = [regex]::Match($runText, 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
L "ip=$ip"

L '######## baseline: sessions + netstat ########'
Set-Content "$dir\rdp1-b.cmd" -Value 'cmd.exe /c qwinsta & echo ---- & netstat -ano & echo ---- & tasklist' -Encoding ASCII
L ((& $exe shf $id "$dir\rdp1-b.cmd" 2>&1) -join "`n")

L '######## set a password for ContainerAdministrator (for a real RDP logon) ########'
Set-Content "$dir\rdp1-pw.cmd" -Value 'cmd.exe /c net user ContainerAdministrator Wsm!Rdp12345 & net user ContainerAdministrator' -Encoding ASCII
L ((& $exe shf $id "$dir\rdp1-pw.cmd" 2>&1) -join "`n")

L '######## apply TCP-RDP recipe (ContainerMode=0 + SecurityLayer=0 + NLA off) ########'
@'
cmd.exe /c reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v ContainerMode /t REG_DWORD /d 0 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v fDenyTSConnections /t REG_DWORD /d 0 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v SecurityLayer /t REG_DWORD /d 0 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v UserAuthentication /t REG_DWORD /d 0 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v MinEncryptionLevel /t REG_DWORD /d 1 /f & sc config TermService start= auto & net start TermService
'@ | Set-Content "$dir\rdp1-a.cmd" -Encoding ASCII
L ((& $exe shf $id "$dir\rdp1-a.cmd" 2>&1) -join "`n")

L '######## restart TermService so ContainerMode=0 takes effect ########'
Set-Content "$dir\rdp1-b2.cmd" -Value 'cmd.exe /c net stop TermService /y & echo ---- & net start TermService & net start UmRdpService & net start SessionEnv' -Encoding ASCII
L ((& $exe shf $id "$dir\rdp1-b2.cmd" 2>&1) -join "`n")

L '######## firewall + listener check ########'
@'
cmd.exe /c netsh advfirewall firewall delete rule name=WsmRdp3389 & netsh advfirewall firewall add rule name=WsmRdp3389 protocol=TCP dir=in localport=3389 action=allow & netsh advfirewall show allprofiles state & echo ==== & netstat -ano & echo ==== & qwinsta & echo ==== & sc query TermService & sc query UmRdpService
'@ | Set-Content "$dir\rdp1-c.cmd" -Encoding ASCII
Start-Sleep -Seconds 6
L ((& $exe shf $id "$dir\rdp1-c.cmd" 2>&1) -join "`n")

if ($ip) {
  L '######## host -> container :3389 ########'
  L ('tcp 3389: ' + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  L ((& $exe rprobe $ip 3389 2>&1) -join "`n")
}
L '######## done ########'
