$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\ws22.log"
"==== ws22 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$img = 'C:\wsmprobe\img'

"== free space ==" | Add-Content $log
Get-PSDrive C | Select-Object Used,Free | Out-String | Add-Content $log

$base1 = "$img\base1"
if (-not (Test-Path "$base1\Files\Windows\System32\cmd.exe")) {
  "== extracting layer1 ==" | Add-Content $log
  Remove-Item -Recurse -Force $base1 -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force $base1 | Out-Null
  & tar.exe -xf "$img\layer1.tgz" -C $base1 2>&1 | Add-Content $log
  Add-Content $log "tar exit=$LASTEXITCODE"
} else { "layer1 already extracted" | Add-Content $log }

"== layout ==" | Add-Content $log
Get-ChildItem $base1 -Force | Select-Object Name | Out-String | Add-Content $log
Test-Path "$base1\Files\Windows\System32\cmd.exe" | Add-Content $log
Test-Path "$base1\Hives\SOFTWARE" | Add-Content $log

# Capture the base OS build info from the extracted hive
$sw = "$base1\Hives\SOFTWARE"
if (Test-Path $sw) {
  & reg unload "HKLM\WsmWS22" 2>$null | Out-Null
  Add-Content $log ((& reg load "HKLM\WsmWS22" $sw 2>&1) -join "`n")
  Add-Content $log "-- CurrentVersion --"
  & reg query "HKLM\WsmWS22\Microsoft\Windows NT\CurrentVersion" /v CurrentBuild 2>&1 | Add-Content $log
  & reg query "HKLM\WsmWS22\Microsoft\Windows NT\CurrentVersion" /v InstallationType 2>&1 | Add-Content $log
  & reg query "HKLM\WsmWS22\Microsoft\Windows NT\CurrentVersion" /v EditionID 2>&1 | Add-Content $log
  Add-Content $log ((& reg unload "HKLM\WsmWS22" 2>&1) -join "`n")
}

"== argon (schema2) ==" | Add-Content $log
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee00aa"
& $exe argon $id "C:\wsmprobe\ws22a" $base1 "cmd.exe /c whoami && ver" 2>&1 | Add-Content $log
Add-Content $log "== stop =="
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
