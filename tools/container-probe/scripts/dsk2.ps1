$ErrorActionPreference = 'Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\dsk2.log"
"==== dsk2 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$layer = 'C:\wsm-images\servercore-ltsc2022'
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d02'
$scratch = 'C:\wsmprobe\dsk2'

function Log($t) { Add-Content $log $t }

# --- ensure a NAT network for container networking
$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
Log "natId=$natId"

# --- start the container (elevated, holding the handle)
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '1800'
& '$exe' argonnet3 $id $scratch '$layer' '$natId' *> '$dir\dsk2-run.log'
"@
Set-Content "$dir\dsk2-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\dsk2-child.ps1" | Out-Null

Log "waiting for container..."
Start-Sleep -Seconds 55
$runText = Get-Content "$dir\dsk2-run.log" -Raw -ErrorAction SilentlyContinue
Log "-- run log --"
Log $runText
$mi = [regex]::Match($runText, 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
Log "ip=$ip"

# --- basic session checks
Log "==== tasklist ===="
Log ((& $exe sh $id "cmd.exe /c tasklist /v" 2>&1) -join "`n")
Log "==== qwinsta ===="
Log ((& $exe sh $id "cmd.exe /c qwinsta" 2>&1) -join "`n")
Log "==== query session ===="
Log ((& $exe sh $id "cmd.exe /c query session" 2>&1) -join "`n")
Log "==== whoami /all ===="
Log ((& $exe sh $id "cmd.exe /c whoami /all" 2>&1) -join "`n")

# --- deep payload
$b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes((Get-Content "$dir\payload.ps1" -Raw)))
Log "==== payload ===="
Log ((& $exe sh $id "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -EncodedCommand $b64" 2>&1) -join "`n")

# --- try to start winlogon / userinit manually
Log "==== try start winlogon ===="
Log ((& $exe sh $id "cmd.exe /c C:\Windows\System32\winlogon.exe" 2>&1) -join "`n")
Log "==== try start userinit ===="
Log ((& $exe sh $id "cmd.exe /c C:\Windows\System32\userinit.exe" 2>&1) -join "`n")
Log "==== try start LogonUI ===="
Log ((& $exe sh $id "cmd.exe /c C:\Windows\System32\LogonUI.exe" 2>&1) -join "`n")
Log "==== tasklist after attempts ===="
Log ((& $exe sh $id "cmd.exe /c tasklist /v" 2>&1) -join "`n")

Log "==== done ===="
