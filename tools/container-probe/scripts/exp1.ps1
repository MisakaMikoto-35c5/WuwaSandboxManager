$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\exp1.log"
"==== exp1 $(Get-Date -Format o) ====" | Set-Content $log
$id     = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0001"
$layerId = "6a50e60e-b4b0-45cc-abc7-87d84f6551ea"
$layerRoot = "C:\ProgramData\Microsoft\Windows\Containers\Layers\$layerId"
$scratch = "C:\wsmprobe\scratch1"
New-Item -ItemType Directory -Force -Path $scratch | Out-Null

$cfg = [ordered]@{
  SchemaVersion = @{ Major = 2; Minor = 1 }
  Owner = "WsmProbe"
  ShouldTerminateOnLastHandleClosed = $true
  Container = [ordered]@{
    GuestOs = @{ HostName = "wsmprobe" }
    Storage = @{ Path = $scratch; Layers = @(@{ Id = $layerId; Path = $layerRoot }) }
  }
}
$json = $cfg | ConvertTo-Json -Depth 12 -Compress
$jsonFile = "$env:TEMP\opencode\wsmdiag\c1.json"
Set-Content -Path $jsonFile -Value $json -Encoding UTF8
Add-Content $log "--- config ---"
Add-Content $log $json

$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
foreach ($step in @("create $id $jsonFile", "start $id", "sh $id cmd.exe /c whoami", "sh $id cmd.exe /c ver")) {
  Add-Content $log "--- hcsprobe $step ---"
  & $exe ($step -split ' ') 2>&1 | Add-Content $log
}
Add-Content $log "--- stop ---"
& $exe stop $id 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
