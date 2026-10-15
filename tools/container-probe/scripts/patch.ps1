$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\patch.log"
"==== patch test $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$layer = "C:\ProgramData\Microsoft\Windows\Containers\Layers\6a50e60e-b4b0-45cc-abc7-87d84f6551ea"
$base  = "C:\ProgramData\Microsoft\Windows\Containers\Layers\cf0776b2-3566-4743-90e5-6f0db1f2064c"

# stop any running sandbox compute systems
"== running systems before ==" | Add-Content $log
& hcsdiag list 2>&1 | Add-Content $log
Get-ComputeProcess -ErrorAction SilentlyContinue | ForEach-Object { "terminating $($_.Id)" | Add-Content $log; $_ | Stop-ComputeProcess -Force -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 3

$hives = @(
  "$layer\Specialization\Windows\System32\config\SOFTWARE",
  "$layer\Files\Windows\System32\Config\SOFTWARE"
)
$mk = "HKLM\WsmPatch"
foreach ($h in $hives) {
  Add-Content $log "== patch $h =="
  if (-not (Test-Path $h)) { Add-Content $log "missing"; continue }
  & reg unload $mk 2>$null | Out-Null
  Add-Content $log ((& reg load $mk $h 2>&1) -join "`n")
  $k = "$mk\Microsoft\Windows NT\CurrentVersion"
  & reg add $k /v CurrentBuild /t REG_SZ /d "26300" /f 2>&1 | Add-Content $log
  & reg add $k /v CurrentBuildNumber /t REG_SZ /d "26300" /f 2>&1 | Add-Content $log
  & reg add $k /v DisplayVersion /t REG_SZ /d "26H2" /f 2>&1 | Add-Content $log
  & reg query $k /v CurrentBuild 2>&1 | Add-Content $log
  Add-Content $log ((& reg unload $mk 2>&1) -join "`n")
}

# now try the schema1 container
$scratch = "C:\wsmprobe\p1"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe lsandbox $scratch $base 2>&1 | Add-Content $log
& $exe lactivate $scratch 2>&1 | Add-Content $log
& $exe lmount $scratch 2>&1 | Add-Content $log

$nameGuid = (& $exe lname $base).Trim()
$id = "wsmprobe-patch1"
$obj = [ordered]@{
  SystemType = "Container"; Name = "wsmprobe"; Owner = "WsmProbe"
  LayerFolderPath = $scratch
  Layers = @(@{ Id = $nameGuid; Path = $base })
  HvPartition = $false
  TerminateOnLastHandleClosed = $true
}
$f = "$env:TEMP\opencode\wsmdiag\patch1.json"
Set-Content -Path $f -Value ($obj | ConvertTo-Json -Depth 10 -Compress) -Encoding UTF8
Add-Content $log "== create =="
& $exe create $id $f 2>&1 | Add-Content $log
Add-Content $log "== start =="
$s = & $exe start $id 2>&1
Add-Content $log ($s -join "`n")
if ($s -match 'STARTED OK') {
  Start-Sleep -Seconds 6
  Add-Content $log "== exec =="
  & $exe exec $id "cmd.exe /c whoami && ver" 2>&1 | Add-Content $log
  & $exe exec $id "cmd.exe /c dir C:\\Windows\\System32 | findstr /C:ntdll" 2>&1 | Add-Content $log
}
Add-Content $log "==== done ===="
