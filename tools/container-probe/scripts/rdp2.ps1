$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\rdp2.log"
"==== rdp2 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$sys = "$img\Files\Windows\System32\config\SYSTEM"
$sof = "$img\Files\Windows\System32\config\SOFTWARE"

L '######## stop containers + release the layer ########'
try {
  $vms = ((& hcsdiag.exe list -raw 2>&1) -join "`n") | ConvertFrom-Json
  foreach ($v in $vms) { if ($v.Owner -eq 'WsmProbe') { L ("  stop " + $v.Id); (& $exe stop $v.Id 2>&1) | Out-Null } }
} catch { L ('err ' + $_.Exception.Message) }
Start-Sleep -Seconds 4
L ('unprepare: ' + ((& $exe lunprepare $img 2>&1) -join ' '))
L ('deactivate: ' + ((& $exe ldeactivate $img 2>&1) -join ' '))
Start-Sleep -Seconds 2

L '######## check SOFTWARE policy for Terminal Services ########'
$s2 = "$dir\SOFT.mod"
Copy-Item $sof $s2 -Force -ErrorAction SilentlyContinue
& reg.exe unload 'HKLM\WSM' 2>&1 | Out-Null
L ('load software: ' + ((& reg.exe load 'HKLM\WSM' $s2 2>&1) -join ' '))
L ((& reg.exe query 'HKLM\WSM\Microsoft\Policies\Microsoft\Windows NT\Terminal Services' 2>&1) -join "`n")
L ((& reg.exe query 'HKLM\WSM\Policies\Microsoft\Windows NT\Terminal Services' 2>&1) -join "`n")
& reg.exe unload 'HKLM\WSM' 2>&1 | Out-Null

L '######## patch SYSTEM hive ########'
$mod = "$dir\SYSTEM.rdp"
Copy-Item $sys $mod -Force -ErrorAction SilentlyContinue
L ('copy for patch ok: ' + (Test-Path $mod) + ' size=' + ((Get-Item $mod -ErrorAction SilentlyContinue).Length))
& reg.exe unload 'HKLM\WSR' 2>&1 | Out-Null
L ('load: ' + ((& reg.exe load 'HKLM\WSR' $mod 2>&1) -join ' '))
foreach ($cs in 'ControlSet001','ControlSet002') {
  $ts = "HKLM\WSR\$cs\Control\Terminal Server"
  $rdp = "$ts\WinStations\RDP-Tcp"
  foreach ($cmd in @(
      @('add', $ts, '/v','ContainerMode','/t','REG_DWORD','/d','0','/f'),
      @('add', $ts, '/v','fDenyTSConnections','/t','REG_DWORD','/d','0','/f'),
      @('add', "$ts\WinStations\RDP-Tcp", '/v','SecurityLayer','/t','REG_DWORD','/d','0','/f'),
      @('add', "$ts\WinStations\RDP-Tcp", '/v','UserAuthentication','/t','REG_DWORD','/d','0','/f'),
      @('add', "$ts\WinStations\RDP-Tcp", '/v','fEnableWinStation','/t','REG_DWORD','/d','1','/f'),
      @('add', "HKLM\WSR\$cs\Services\TermService", '/v','Start','/t','REG_DWORD','/d','2','/f'),
      @('add', "HKLM\WSR\$cs\Services\UmRdpService", '/v','Start','/t','REG_DWORD','/d','2','/f')
    )) {
    L ("  $cs " + ($cmd[2..8] -join ' ') + ' -> ' + ((& reg.exe @cmd 2>&1) -join ' '))
  }
}
L ('unload: ' + ((& reg.exe unload 'HKLM\WSR' 2>&1) -join ' '))
Copy-Item $mod $sys -Force -ErrorAction SilentlyContinue
L ('image hive size now: ' + ((Get-Item $sys).Length))

L '######## start container on patched image ########'
$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d0b'
$scratch = 'C:\wsmprobe\dskB'
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '1800'
& '$exe' argonnet3 $id $scratch '$img' '$natId' *> '$dir\rdp2-run.log'
"@
Set-Content "$dir\rdp2-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\rdp2-child.ps1" | Out-Null
Start-Sleep -Seconds 60
$runText = Get-Content "$dir\rdp2-run.log" -Raw -ErrorAction SilentlyContinue
L ('start ok: ' + ($runText -match 'STARTED OK'))
$mi = [regex]::Match($runText, 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
L "ip=$ip"

L '######## container state ########'
@'
cmd.exe /c qwinsta & echo ---- & sc query TermService & sc query UmRdpService & echo ---- & reg query "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v ContainerMode & echo ---- & netstat -ano & echo ---- & tasklist
'@ | Set-Content "$dir\rdp2-a.cmd" -Encoding ASCII
L ((& $exe shf $id "$dir\rdp2-a.cmd" 2>&1) -join "`n")

if ($ip) {
  L '######## host -> container :3389 ########'
  Start-Sleep -Seconds 5
  L ('tcp 3389: ' + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  L ((& $exe rprobe $ip 3389 2>&1) -join "`n")
}
L '######## done ########'
