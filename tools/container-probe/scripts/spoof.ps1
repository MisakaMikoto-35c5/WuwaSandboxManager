$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\spoof.log"
"==== spoof $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base = "C:\ProgramData\Microsoft\Windows\Containers\Layers\cf0776b2-3566-4743-90e5-6f0db1f2064c"
$mk = "HKLM\WsmSpoof"

function Patch-Software([string]$hivePath, [string]$tag) {
  Add-Content $log "== SOFTWARE $tag $hivePath =="
  if (-not (Test-Path $hivePath)) { Add-Content $log "missing"; return }
  & reg unload $mk 2>$null | Out-Null
  Add-Content $log ((& reg load $mk $hivePath 2>&1) -join "`n")
  $k = "$mk\Microsoft\Windows NT\CurrentVersion"
  & reg add $k /v CurrentBuild /t REG_SZ /d "26300" /f 2>&1 | Out-Null
  & reg add $k /v CurrentBuildNumber /t REG_SZ /d "26300" /f 2>&1 | Out-Null
  & reg add $k /v DisplayVersion /t REG_SZ /d "26H2" /f 2>&1 | Out-Null
  & reg add $k /v InstallationType /t REG_SZ /d "Server" /f 2>&1 | Out-Null
  & reg add $k /v EditionID /t REG_SZ /d "ServerStandard" /f 2>&1 | Out-Null
  & reg add $k /v CompositionEditionID /t REG_SZ /d "ServerStandard" /f 2>&1 | Out-Null
  & reg add $k /v ProductName /t REG_SZ /d "Windows Server 2022 Standard" /f 2>&1 | Out-Null
  & reg query $k /v InstallationType 2>&1 | Add-Content $log
  Add-Content $log ((& reg unload $mk 2>&1) -join "`n")
}

function Patch-System([string]$hivePath, [string]$tag) {
  Add-Content $log "== SYSTEM $tag $hivePath =="
  if (-not (Test-Path $hivePath)) { Add-Content $log "missing"; return }
  & reg unload $mk 2>$null | Out-Null
  Add-Content $log ((& reg load $mk $hivePath 2>&1) -join "`n")
  foreach ($cs in @('ControlSet001','CurrentControlSet','ControlSet002')) {
    $k = "$mk\$cs\Control\ProductOptions"
    & reg add $k /v ProductType /t REG_SZ /d "ServerNT" /f 2>&1 | Out-Null
    & reg add $k /v ProductSuite /t REG_SZ /d "Enterprise" /f 2>&1 | Out-Null
  }
  & reg query "$mk\ControlSet001\Control\ProductOptions" /v ProductType 2>&1 | Add-Content $log
  Add-Content $log ((& reg unload $mk 2>&1) -join "`n")
}

Patch-Software "$base\Specialization\Windows\System32\config\SOFTWARE" "spec"
Patch-Software "$base\Files\Windows\System32\Config\SOFTWARE" "base"
Patch-System "$base\Specialization\Windows\System32\config\SYSTEM" "spec"

Add-Content $log "== argon with spoofed client image =="
$id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee00d0"
$scratch = "C:\wsmprobe\spoof1"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe argon $id $scratch $base "cmd.exe /c whoami && ver" 2>&1 | Add-Content $log
Add-Content $log "== stop =="
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
