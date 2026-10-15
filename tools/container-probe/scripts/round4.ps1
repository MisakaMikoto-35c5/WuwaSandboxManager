$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\round4.log"
"==== round4 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d09'
$scratch = 'C:\wsmprobe\dsk9'

$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '420'
& '$exe' argonnet3 $id $scratch '$img' '$natId' *> '$dir\round4-run.log'
"@
Set-Content "$dir\round4-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\round4-child.ps1" | Out-Null
Start-Sleep -Seconds 50
$runText = Get-Content "$dir\round4-run.log" -Raw -ErrorAction SilentlyContinue
L ('start ok: ' + ($runText -match 'STARTED OK'))

# enable RDP first
Set-Content "$dir\r4a.cmd" -Value 'cmd.exe /c sc config TermService start= auto & net start TermService & net start UmRdpService & net start SessionEnv' -Encoding ASCII
L ((& $exe shf $id "$dir\r4a.cmd" 2>&1) -join "`n")
Start-Sleep -Seconds 10

L '######## all listeners ########'
Set-Content "$dir\r4b.cmd" -Value 'cmd.exe /c netstat -ano & echo ==== & qwinsta' -Encoding ASCII
L ((& $exe shf $id "$dir\r4b.cmd" 2>&1) -join "`n")

L '######## service states ########'
Set-Content "$dir\r4c.cmd" -Value 'cmd.exe /c sc query TermService & sc query UmRdpService & sc query SessionEnv & sc qc UmRdpService & echo ==== & reg query "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" & reg query "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v fEnableWinStation' -Encoding ASCII
L ((& $exe shf $id "$dir\r4c.cmd" 2>&1) -join "`n")

L '######## relevant event log entries ########'
Set-Content "$dir\r4d.cmd" -Value 'cmd.exe /c wevtutil qe System /c:60 /rd:true /f:text' -Encoding ASCII
$ev = ((& $exe shf $id "$dir\r4d.cmd" 2>&1) -join "`n")
$ev2 = ($ev -split "`n" | Where-Object { $_ -match 'Term|RDP|Remote|Session|rdp|Listener|拒绝|失败|fail' }) -join "`n"
L $ev2
L '######## done ########'
