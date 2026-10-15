$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\e2.log"
"==== e2 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base1 = 'C:\wsmprobe\img\base1'
$dir = "$env:TEMP\opencode\wsmdiag"
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0e02"
$scratch = "C:\wsmprobe\e2"

$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$json = ($out -split "`n" | Select-Object -Skip 1) -join "`n"
$natId = (($json | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1).ID
Add-Content $log "natId=$natId"
if (-not $natId) { exit }

Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '420'
& '$exe' argonnet3 $id $scratch '$base1' $natId *> '$dir\e2-run.log'
"@
Set-Content "$dir\e2-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\e2-child.ps1" | Out-Null

Start-Sleep -Seconds 45
$runText = Get-Content "$dir\e2-run.log" -Raw -ErrorAction SilentlyContinue
$m = [regex]::Match($runText, 'IPv4 Address[^:]*:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($m.Success) { $m.Groups[1].Value } else { $null }
Add-Content $log "ip=$ip"

# RDP configuration, written with intact quotes
$setup = @'
cmd.exe /c reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v fDenyTSConnections /t REG_DWORD /d 0 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v ContainerMode /t REG_DWORD /d 0 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v PortNumber /t REG_DWORD /d 3389 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v fEnableWinStation /t REG_DWORD /d 1 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v UserAuthentication /t REG_DWORD /d 0 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v SecurityLayer /t REG_DWORD /d 0 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v MinEncryptionLevel /t REG_DWORD /d 1 /f & sc config TermService start= auto & net start TermService
'@
Set-Content "$dir\e2-setup.cmd" -Value $setup -Encoding ASCII
Add-Content $log "--- setup"
Add-Content $log ((& $exe shf $id "$dir\e2-setup.cmd" 2>&1) -join "`n")

$verify = 'cmd.exe /c reg query "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations" /s'
Set-Content "$dir\e2-verify.cmd" -Value $verify -Encoding ASCII
Add-Content $log "--- verify WinStations"
Add-Content $log ((& $exe shf $id "$dir\e2-verify.cmd" 2>&1) -join "`n")

$services = 'cmd.exe /c sc query TermService & sc query UmRdpService & sc query SessionEnv'
Set-Content "$dir\e2-services.cmd" -Value $services -Encoding ASCII
Add-Content $log "--- services"
Add-Content $log ((& $exe shf $id "$dir\e2-services.cmd" 2>&1) -join "`n")

Start-Sleep -Seconds 5
if ($ip) {
  Add-Content $log "== tcp 3389 =="
  Add-Content $log ("tcp: " + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  Add-Content $log "== rdp handshake =="
  Add-Content $log ((& $exe rprobe $ip 3389 2>&1) -join "`n")
}

Add-Content $log "== stop =="
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="

