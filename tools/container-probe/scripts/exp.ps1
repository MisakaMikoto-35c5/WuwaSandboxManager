$ErrorActionPreference = 'Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\exp.log"
"==== exp $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsm-images\servercore-ltsc2022'
$sys = "$img\Files\Windows\System32\config\SYSTEM"
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d05'
$scratch = 'C:\wsmprobe\dsk5'

L '######## patch image hive: NumberOfInitialSessions 1 -> 2 ########'
Copy-Item $sys "$dir\SYSTEM.orig" -Force
Copy-Item $sys "$dir\SYSTEM.mod" -Force
& reg.exe unload 'HKLM\WSMMOD' 2>&1 | Out-Null
L ('load: ' + ((& reg.exe load 'HKLM\WSMMOD' "$dir\SYSTEM.mod" 2>&1) -join ' '))
foreach ($cs in 'ControlSet001','ControlSet002') {
  L ('add ' + ((& reg.exe add "HKLM\WSMMOD\$cs\Control\Session Manager" /v NumberOfInitialSessions /t REG_DWORD /d 2 /f 2>&1) -join ' '))
}
L ('unload: ' + ((& reg.exe unload 'HKLM\WSMMOD' 2>&1) -join ' '))
Copy-Item "$dir\SYSTEM.mod" $sys -Force
L '--- verify patched image hive ---'
& reg.exe unload 'HKLM\WSMMOD' 2>&1 | Out-Null
& reg.exe load 'HKLM\WSMMOD' $sys 2>&1 | Out-Null
L ((& reg.exe query 'HKLM\WSMMOD\ControlSet001\Control\Session Manager' /v NumberOfInitialSessions 2>&1) -join "`n")
& reg.exe unload 'HKLM\WSMMOD' 2>&1 | Out-Null

L '######## start container on patched image ########'
$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
Log "natId=$natId"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '900'
& '$exe' argonnet3 $id $scratch '$img' '$natId' *> '$dir\exp-run.log'
"@
Set-Content "$dir\exp-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\exp-child.ps1" | Out-Null
Start-Sleep -Seconds 55
$runText = Get-Content "$dir\exp-run.log" -Raw -ErrorAction SilentlyContinue
L ('start ok: ' + ($runText -match 'STARTED OK'))
L '--- run log (errors?) ---'
L (($runText -split "`n" | Where-Object { $_ -match 'hr=|error|fail|STARTED' }) -join "`n")

L '######## tasklist ########'
L ((& $exe sh $id "cmd.exe /c tasklist" 2>&1) -join "`n")
L '######## qwinsta / query session ########'
L ((& $exe sh $id "cmd.exe /c qwinsta & query session & query user" 2>&1) -join "`n")
L '######## live NumberOfInitialSessions ########'
L ((& $exe sh $id "cmd.exe /c reg query \"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\" /v NumberOfInitialSessions" 2>&1) -join "`n")
L '######## p1 (processes/logons) ########'
$b1 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes((Get-Content "$dir\p1.ps1" -Raw)))
L ((& $exe sh $id "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -EncodedCommand $b1" 2>&1) -join "`n")
L '######## p3 (window station / display) ########'
$b3 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes((Get-Content "$dir\p3.ps1" -Raw)))
L ((& $exe sh $id "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -EncodedCommand $b3" 2>&1) -join "`n")
L '######## done ########'
