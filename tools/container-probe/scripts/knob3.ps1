$ErrorActionPreference='Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\knob3.log"
"==== knob3 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$sof = "$img\Files\Windows\System32\config\SOFTWARE"

L '--- stop containers + release layer ---'
try { $vms = ((& hcsdiag.exe list -raw 2>&1) -join "`n") | ConvertFrom-Json
  foreach ($v in $vms) { if ($v.Owner -eq 'WsmProbe') { L ("  stop " + $v.Id); (& $exe stop $v.Id 2>&1) | Out-Null } } } catch { L ('err ' + $_.Exception.Message) }
Start-Sleep -Seconds 4
L ('deactivate: ' + ((& $exe ldeactivate $img 2>&1) -join ' '))
Start-Sleep -Seconds 2

L '--- patch SOFTWARE: value under Microsoft\Windows NT\CurrentVersion ---'
Copy-Item $sof "$dir\SOFT.knob3" -Force -ErrorAction SilentlyContinue
& reg.exe unload 'HKLM\WSK3' 2>&1 | Out-Null
L ('load: ' + ((& reg.exe load 'HKLM\WSK3' "$dir\SOFT.knob3" 2>&1) -join ' '))
$cv = 'HKLM\WSK3\Microsoft\Windows NT\CurrentVersion'
L ('dword:  ' + ((& reg.exe add $cv /v AllowListenersInsideArgonContainer /t REG_DWORD /d 1 /f 2>&1) -join ' '))
L ('under TerminalServer: ' + ((& reg.exe add "$cv\TerminalServer" /v AllowListenersInsideArgonContainer /t REG_DWORD /d 1 /f 2>&1) -join ' '))
L ((& reg.exe query $cv /v AllowListenersInsideArgonContainer 2>&1) -join "`n")
L ((& reg.exe query $cv /v EditionID 2>&1) -join "`n")
L ('unload: ' + ((& reg.exe unload 'HKLM\WSK3' 2>&1) -join ' '))
Copy-Item "$dir\SOFT.knob3" $sof -Force -ErrorAction SilentlyContinue

L '--- start container ---'
$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d0f'
$scratch = 'C:\wsmprobe\dskF'
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '7200'
& '$exe' argonnet3 $id $scratch '$img' '$natId' *> '$dir\knob3-run.log'
"@
Set-Content "$dir\knob3-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\knob3-child.ps1" | Out-Null
Start-Sleep -Seconds 60
$runText = Get-Content "$dir\knob3-run.log" -Raw -ErrorAction SilentlyContinue
L ('start ok: ' + ($runText -match 'STARTED OK'))
$mi = [regex]::Match($runText, 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
L "ip=$ip"

L '--- inside: qwinsta / value visible? ---'
@'
cmd.exe /c qwinsta & echo ---- & reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v AllowListenersInsideArgonContainer & echo ---- & reg query "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v AllowListenersInsideArgonContainer
'@ | Set-Content "$dir\k3-a.cmd" -Encoding ASCII
L ((& $exe shf $id "$dir\k3-a.cmd" 2>&1) -join "`n")

if ($ip) {
  L '--- host -> container :3389 ---'
  L ('  tcp 3389: ' + (Test-NetConnection -ComputerName $ip -Port 3389 -WarningAction SilentlyContinue).TcpTestSucceeded)
  L ((& $exe rprobe $ip 3389 2>&1) -join "`n")
}
L '######## done ########'
