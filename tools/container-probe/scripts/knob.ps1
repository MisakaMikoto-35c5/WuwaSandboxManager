$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\knob.log"
"==== knob $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$sys = "$img\Files\Windows\System32\config\SYSTEM"
$sof = "$img\Files\Windows\System32\config\SOFTWARE"

L '--- why did 0d0c die? holder log tail ---'
L ((Get-Content "$dir\rdp5-run.log" -Tail 6 -ErrorAction SilentlyContinue) -join "`n")

L '--- stop containers + release layer ---'
try { $vms = ((& hcsdiag.exe list -raw 2>&1) -join "`n") | ConvertFrom-Json
  foreach ($v in $vms) { if ($v.Owner -eq 'WsmProbe') { L ("  stop " + $v.Id); (& $exe stop $v.Id 2>&1) | Out-Null } } } catch { L ('err ' + $_.Exception.Message) }
Start-Sleep -Seconds 4
L ('unprepare: ' + ((& $exe lunprepare $img 2>&1) -join ' '))
L ('deactivate: ' + ((& $exe ldeactivate $img 2>&1) -join ' '))
Start-Sleep -Seconds 2

L '--- image SOFTWARE: EditionID etc ---'
Copy-Item $sof "$dir\SOFT.cur" -Force -ErrorAction SilentlyContinue
& reg.exe unload 'HKLM\WSMS' 2>&1 | Out-Null
L ('load: ' + ((& reg.exe load 'HKLM\WSMS' "$dir\SOFT.cur" 2>&1) -join ' '))
foreach ($v in 'EditionID','ProductName','InstallationType','CompositionEditionID') {
  L ((& reg.exe query 'HKLM\WSMS\Microsoft\Windows NT\CurrentVersion' /v $v 2>&1) -join "`n")
}
L ((& reg.exe query 'HKLM\WSMS\Microsoft\Windows NT\CurrentVersion\Server\ServerLevels' 2>&1) -join "`n")
& reg.exe unload 'HKLM\WSMS' 2>&1 | Out-Null

L '--- patch SYSTEM: AllowListenersInsideArgonContainer=1 (plus earlier RDP settings) ---'
Copy-Item $sys "$dir\SYSTEM.knob" -Force -ErrorAction SilentlyContinue
& reg.exe unload 'HKLM\WSK' 2>&1 | Out-Null
L ('load: ' + ((& reg.exe load 'HKLM\WSK' "$dir\SYSTEM.knob" 2>&1) -join ' '))
foreach ($cs in 'ControlSet001','ControlSet002') {
  $ts = "HKLM\WSK\$cs\Control\Terminal Server"
  foreach ($cmd in @(
      @('add', $ts, '/v','AllowListenersInsideArgonContainer','/t','REG_DWORD','/d','1','/f'),
      @('add', "$ts\WinStations\RDP-Tcp", '/v','AllowListenersInsideArgonContainer','/t','REG_DWORD','/d','1','/f'),
      @('add', $ts, '/v','ContainerMode','/t','REG_DWORD','/d','0','/f'),
      @('add', $ts, '/v','fDenyTSConnections','/t','REG_DWORD','/d','0','/f'),
      @('add', "$ts\WinStations\RDP-Tcp", '/v','SecurityLayer','/t','REG_DWORD','/d','0','/f'),
      @('add', "$ts\WinStations\RDP-Tcp", '/v','UserAuthentication','/t','REG_DWORD','/d','0','/f'),
      @('add', "$ts\WinStations\RDP-Tcp", '/v','fEnableWinStation','/t','REG_DWORD','/d','1','/f'),
      @('add', "HKLM\WSK\$cs\Services\TermService", '/v','Start','/t','REG_DWORD','/d','2','/f')
    )) { L ("  " + ($cmd[3]) + ' -> ' + ((& reg.exe @cmd 2>&1) -join ' ')) }
}
L ('unload: ' + ((& reg.exe unload 'HKLM\WSK' 2>&1) -join ' '))
Copy-Item "$dir\SYSTEM.knob" $sys -Force -ErrorAction SilentlyContinue
L ('image SYSTEM size: ' + ((Get-Item $sys).Length))

L '--- start container ---'
$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d0d'
$scratch = 'C:\wsmprobe\dskD'
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '7200'
& '$exe' argonnet3 $id $scratch '$img' '$natId' *> '$dir\knob-run.log'
"@
Set-Content "$dir\knob-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\knob-child.ps1" | Out-Null
Start-Sleep -Seconds 60
$runText = Get-Content "$dir\knob-run.log" -Raw -ErrorAction SilentlyContinue
L ('start ok: ' + ($runText -match 'STARTED OK'))
$mi = [regex]::Match($runText, 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
L "ip=$ip"

L '--- qwinsta / value / services ---'
@'
cmd.exe /c qwinsta & echo ---- & reg query "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v AllowListenersInsideArgonContainer & echo ---- & sc query TermService & sc query UmRdpService
'@ | Set-Content "$dir\k-a.cmd" -Encoding ASCII
L ((& $exe shf $id "$dir\k-a.cmd" 2>&1) -join "`n")

if ($ip) {
  L '--- host -> container :3389 ---'
  L ('  tcp 3389: ' + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  L ((& $exe rprobe $ip 3389 2>&1) -join "`n")
}
L '######## done ########'
