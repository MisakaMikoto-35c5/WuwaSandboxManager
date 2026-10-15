$ErrorActionPreference = 'Continue'
$dir = "$env:TEMP\opencode\wsmdiag"
$log = "$dir\dsk3.log"
"==== dsk3 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$layer = 'C:\wsm-images\servercore-ltsc2022'
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0d03'
$scratch = 'C:\wsmprobe\dsk3'
function Log($t) { Add-Content $log $t }

# host contrast
Log '======== HOST (for contrast) ========'
try { Log ((Get-CimInstance Win32_VideoController | Select-Object Name,Status,PNPDeviceID | Format-List | Out-String)) } catch { Log ('ERR ' + $_) }
Log ("host SM_CXSCREEN=" + [int](Add-Type -MemberDefinition '[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);' -Name H -Namespace Q -PassThru)::GetSystemMetrics(0))

# nat network
$out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
$natId = $null
try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {}
if (-not $natId) { & $exe hnsnat 2>&1 | Out-Null
  $out = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
  try { $natId = ((($out -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output | Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID } catch {} }
Log "natId=$natId"

if (-not (Get-Process | Where-Object { $_.ProcessName -eq 'powershell' -and $_.Id -ne $PID })) { }
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$child = @"
`$env:WSMPROBE_HOLD = '1800'
& '$exe' argonnet3 $id $scratch '$layer' '$natId' *> '$dir\dsk3-run.log'
"@
Set-Content "$dir\dsk3-child.ps1" -Value $child -Encoding UTF8
Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile","-ExecutionPolicy","Bypass","-File","$dir\dsk3-child.ps1" | Out-Null
Log 'waiting for container...'
Start-Sleep -Seconds 50
$runText = Get-Content "$dir\dsk3-run.log" -Raw -ErrorAction SilentlyContinue
Log ("start ok: " + ($runText -match 'STARTED OK'))
$mi = [regex]::Match($runText, 'IPv4[^\r\n]*?:\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
$ip = if ($mi.Success) { $mi.Groups[1].Value } else { $null }
Log "ip=$ip"
Log "==== alive check (tasklist) ===="
Log ((& $exe sh $id "cmd.exe /c tasklist" 2>&1) -join "`n")

foreach ($p in 'p1','p2','p3','p4') {
  $b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes((Get-Content "$dir\$p.ps1" -Raw)))
  Log "======== $p ========"
  Log ((& $exe sh $id "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -NonInteractive -EncodedCommand $b64" 2>&1) -join "`n")
}

Log '======== try starting winlogon.exe ========'
Log ((& $exe sh $id "cmd.exe /c C:\Windows\System32\winlogon.exe" 2>&1) -join "`n")
Log '======== try starting userinit.exe ========'
Log ((& $exe sh $id "cmd.exe /c C:\Windows\System32\userinit.exe" 2>&1) -join "`n")
Log '======== try starting LogonUI.exe ========'
Log ((& $exe sh $id "cmd.exe /c C:\Windows\System32\LogonUI.exe" 2>&1) -join "`n")
Log '======== tasklist after attempts ========'
Log ((& $exe sh $id "cmd.exe /c tasklist" 2>&1) -join "`n")
Log '======== recent system event log errors ========'
Log ((& $exe sh $id "cmd.exe /c wevtutil qe System /c:15 /rd:true /f:text /q:*[System[Level=1 or Level=2]]" 2>&1) -join "`n")
Log '======== rdp port after TermService start ========'
$setup = 'cmd.exe /c sc config TermService start= auto & net start TermService & net start UmRdpService & net start SessionEnv & netstat -an | findstr 3389 & qwinsta'
Log ((& $exe sh $id $setup 2>&1) -join "`n")
Log "==== done ===="
