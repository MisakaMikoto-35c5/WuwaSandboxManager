$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\e1.log"
"==== e1 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base1 = 'C:\wsmprobe\img\base1'
$dir = "$env:TEMP\opencode\wsmdiag"
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0e01"
$scratch = "C:\wsmprobe\e1"

Add-Content $log "base1 exists: $(Test-Path "$base1\Files\Windows\System32\cmd.exe")"

# NAT network id
$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$json = ($out -split "`n" | Select-Object -Skip 1) -join "`n"
$natId = (($json | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1).ID
Add-Content $log "natId=$natId"
if (-not $natId) { Add-Content $log "no NAT network"; Add-Content $log "==== done ===="; exit }

Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue

# launch the container holder in a child process
$child = @"
`$env:WSMPROBE_HOLD = '1500'
& '$exe' argonnet3 $id $scratch '$base1' $natId *> '$dir\e1-run.log'
"@
$childPath = "$dir\e1-child.ps1"
Set-Content $childPath -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File",$childPath | Out-Null

Add-Content $log "waiting for container..."
Start-Sleep -Seconds 45
Add-Content $log "-- run log --"
Add-Content $log ((Get-Content "$dir\e1-run.log" -Raw -ErrorAction SilentlyContinue))

# derive the container IP from ipconfig in the run log
$ip = $null
$m = [regex]::Match((Get-Content "$dir\e1-run.log" -Raw -ErrorAction SilentlyContinue), 'IPv4 Address[^:]*:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
if ($m.Success) { $ip = $m.Groups[1].Value }
Add-Content $log "container ip=$ip"

$cmds = @(
  'cmd.exe /c reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v fDenyTSConnections /t REG_DWORD /d 0 /f',
  'cmd.exe /c reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v ContainerMode /t REG_DWORD /d 0 /f',
  'cmd.exe /c reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v PortNumber /t REG_DWORD /d 3389 /f',
  'cmd.exe /c reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v fEnableWinStation /t REG_DWORD /d 1 /f',
  'cmd.exe /c reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v UserAuthentication /t REG_DWORD /d 0 /f',
  'cmd.exe /c reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v SecurityLayer /t REG_DWORD /d 0 /f',
  'cmd.exe /c reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v MinEncryptionLevel /t REG_DWORD /d 1 /f',
  'cmd.exe /c sc stop TermService',
  'cmd.exe /c sc start TermService',
  'cmd.exe /c sc start UmRdpService',
  'cmd.exe /c sc start SessionEnv',
  'cmd.exe /c netstat -an'
)
foreach ($c in $cmds) {
  Add-Content $log "--- $c"
  Add-Content $log ((& $exe sh $id $c 2>&1) -join "`n")
}

if ($ip) {
  Add-Content $log "== host tcp 3389 test =="
  $ok = (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded
  Add-Content $log "$ip :3389 = $ok"
}

Add-Content $log "== stop =="
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
