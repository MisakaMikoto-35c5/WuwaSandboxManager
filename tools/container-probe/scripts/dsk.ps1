$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\dsk.log"
"==== dsk $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\server-ltsc2022.src'
$layer = 'C:\wsm-images\server-ltsc2022'
$dir = "$env:TEMP\opencode\wsmdiag"

# 1. Extract the Desktop Experience base layer and prepare it.
if (-not (Test-Path "$layer\Files\Windows\explorer.exe")) {
  Add-Content $log "== extracting =="
  Remove-Item -Recurse -Force $layer -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force $layer | Out-Null
  & tar.exe -xf "$img\layer1.tgz" -C $layer 2>&1 | Add-Content $log
  Add-Content $log "tar exit=$LASTEXITCODE"
}
Add-Content $log "== processbase =="
Add-Content $log ((& $exe lprocessbase $layer 2>&1) -join "`n")

Add-Content $log "== desktop binaries present =="
foreach ($f in 'explorer.exe','dwm.exe','LogonUI.exe','winlogon.exe','cmd.exe','ServerManager.exe','mmc.exe','mstsc.exe','termsrv.dll','rdpbase.dll','TabSvc.dll','sihost.exe','shell32.dll','windows.ui.xaml.dll') {
  Add-Content $log ("  {0,-18} {1}" -f $f, (Test-Path "$layer\Files\Windows\System32\$f"))
}
Add-Content $log ("ntoskrnl: " + (Get-Item "$layer\Files\Windows\System32\ntoskrnl.exe").VersionInfo.FileVersion)

# 2. Process-isolated container with networking.
$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$json = ($out -split "`n" | Select-Object -Skip 1) -join "`n"
$natId = (($json | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1).ID
Add-Content $log "natId=$natId"
if (-not $natId) {
  & $exe hnsnat 2>&1 | Add-Content $log
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  $json = ($out -split "`n" | Select-Object -Skip 1) -join "`n"
  $natId = (($json | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1).ID
  Add-Content $log "natId2=$natId"
}

$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d01"
$scratch = "C:\wsmprobe\dsk1"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '900'
& '$exe' argonnet3 $id $scratch '$layer' $natId *> '$dir\dsk-run.log'
"@
Set-Content "$dir\dsk-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\dsk-child.ps1" | Out-Null

Add-Content $log "waiting 60s for container..."
Start-Sleep -Seconds 60
Add-Content $log "-- run log --"
Add-Content $log (Get-Content "$dir\dsk-run.log" -Raw -ErrorAction SilentlyContinue)
$runText = Get-Content "$dir\dsk-run.log" -Raw -ErrorAction SilentlyContinue
$mi = [regex]::Match($runText, 'IPv4 Address[^:]*:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
Add-Content $log "ip=$ip"

# 3. RDP + session hacks inside the container.
$setup = @'
cmd.exe /c reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v fDenyTSConnections /t REG_DWORD /d 0 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v ContainerMode /t REG_DWORD /d 0 /f & reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services" /v fAllowUnlistedRemotePrograms /t REG_DWORD /d 1 /f & reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Terminal Server\TSAppAllowList" /v fDisabledAllowList /t REG_DWORD /d 1 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v PortNumber /t REG_DWORD /d 3389 /f & reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v UserAuthentication /t REG_DWORD /d 0 /f & sc config TermService start= auto & net start TermService & net start UmRdpService & net start SessionEnv
'@
Set-Content "$dir\dsk-setup.cmd" -Value $setup -Encoding ASCII
Add-Content $log "== rdp setup =="
Add-Content $log ((& $exe shf $id "$dir\dsk-setup.cmd" 2>&1) -join "`n")

Start-Sleep -Seconds 8
Add-Content $log "== qwinsta =="
Add-Content $log ((& $exe sh $id "cmd.exe /c qwinsta" 2>&1) -join "`n")
Add-Content $log "== netstat 3389 =="
Add-Content $log ((& $exe sh $id "cmd.exe /c netstat -an | findstr 3389" 2>&1) -join "`n")
Add-Content $log "== RDP-Tcp =="
Set-Content "$dir\dsk-rdp.cmd" -Value 'cmd.exe /c reg query "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v PortNumber & sc query TermService' -Encoding ASCII
Add-Content $log ((& $exe shf $id "$dir\dsk-rdp.cmd" 2>&1) -join "`n")

if ($ip) {
  Add-Content $log "== host tcp 3389 =="
  Add-Content $log ("tcp: " + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  Add-Content $log ((& $exe rprobe $ip 3389 2>&1) -join "`n")
}

Add-Content $log "== stop =="
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
