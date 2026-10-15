$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\restore.log"
"==== restore $(Get-Date -Format o) ====" | Set-Content $log
$mk = "HKLM\WsmRestore"

$layers = @(
  "C:\ProgramData\Microsoft\Windows\Containers\Layers\cf0776b2-3566-4743-90e5-6f0db1f2064c",
  "C:\ProgramData\Microsoft\Windows\Containers\Layers\6a50e60e-b4b0-45cc-abc7-87d84f6551ea"
)

foreach ($layer in $layers) {
  foreach ($h in @(
    "$layer\Specialization\Windows\System32\config\SOFTWARE",
    "$layer\Files\Windows\System32\Config\SOFTWARE")) {
    if (-not (Test-Path $h)) { continue }
    Add-Content $log "restore SOFTWARE $h"
    & reg unload $mk 2>$null | Out-Null
    Add-Content $log ((& reg load $mk $h 2>&1) -join "`n")
    $k = "$mk\Microsoft\Windows NT\CurrentVersion"
    & reg add $k /v CurrentBuild /t REG_SZ /d "26100" /f 2>&1 | Out-Null
    & reg add $k /v CurrentBuildNumber /t REG_SZ /d "26100" /f 2>&1 | Out-Null
    & reg add $k /v DisplayVersion /t REG_SZ /d "24H2" /f 2>&1 | Out-Null
    & reg add $k /v InstallationType /t REG_SZ /d "Client" /f 2>&1 | Out-Null
    & reg add $k /v EditionID /t REG_SZ /d "Enterprise" /f 2>&1 | Out-Null
    & reg add $k /v CompositionEditionID /t REG_SZ /d "Enterprise" /f 2>&1 | Out-Null
    & reg add $k /v ProductName /t REG_SZ /d "Windows 10 Enterprise" /f 2>&1 | Out-Null
    & reg query $k /v CurrentBuild 2>&1 | Add-Content $log
    & reg query $k /v InstallationType 2>&1 | Add-Content $log
    Add-Content $log ((& reg unload $mk 2>&1) -join "`n")
  }

  $sy = "$layer\Specialization\Windows\System32\config\SYSTEM"
  if (Test-Path $sy) {
    Add-Content $log "restore SYSTEM $sy"
    & reg unload $mk 2>$null | Out-Null
    Add-Content $log ((& reg load $mk $sy 2>&1) -join "`n")
    foreach ($cs in @('ControlSet001','CurrentControlSet')) {
      $k = "$mk\$cs\Control\ProductOptions"
      & reg add $k /v ProductType /t REG_SZ /d "WinNT" /f 2>&1 | Out-Null
      & reg delete $k /v ProductSuite /f 2>&1 | Out-Null
    }
    & reg query "$mk\ControlSet001\Control\ProductOptions" /v ProductType 2>&1 | Add-Content $log
    Add-Content $log ((& reg unload $mk 2>&1) -join "`n")
  }
}
Add-Content $log "==== done ===="
