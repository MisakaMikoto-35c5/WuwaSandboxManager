$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\exp2.log"
"==== exp2 $(Get-Date -Format o) ====" | Set-Content $log
try { $w = New-Object System.ComponentModel.Win32Exception(0x1126); Add-Content $log "0x1126 => $($w.Message)" } catch {}
Add-Content $log ("0x80071126 as int = {0}" -f (0x80071126 - 0x80070000))

$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$dir = "$env:TEMP\opencode\wsmdiag"
$layerId = "6a50e60e-b4b0-45cc-abc7-87d84f6551ea"
$root = "C:\ProgramData\Microsoft\Windows\Containers\Layers\$layerId"
$files = "$root\Files"

New-Item -ItemType Directory -Force -Path "C:\wsmprobe\plain" | Out-Null

function Try-Cfg([string]$name, $cfg) {
  $id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0002"
  $json = $cfg | ConvertTo-Json -Depth 12 -Compress
  $f = "$dir\c_$name.json"
  Set-Content -Path $f -Value $json -Encoding UTF8
  Add-Content $log "===== $name ====="
  Add-Content $log $json
  & $exe create $id $f 2>&1 | Add-Content $log
  & $exe stop $id 2>&1 | Out-Null
  Start-Sleep -Milliseconds 300
}

$base = { param($layerPath, $scratch) 
  [ordered]@{
    SchemaVersion = @{ Major = 2; Minor = 1 }
    Owner = "WsmProbe"
    ShouldTerminateOnLastHandleClosed = $true
    Container = [ordered]@{
      GuestOs = @{ HostName = "wsmprobe" }
      Storage = @{ Path = $scratch; Layers = @(@{ Id = $layerId; Path = $layerPath }) }
    }
  }
}

Try-Cfg "root_plain" (& $base $root "C:\wsmprobe\plain")
Try-Cfg "files_plain" (& $base $files "C:\wsmprobe\plain")
Try-Cfg "root_missing" (& $base $root "C:\wsmprobe\missing1")
Try-Cfg "files_missing" (& $base $files "C:\wsmprobe\missing2")
Try-Cfg "noguest_os" ([ordered]@{
    SchemaVersion = @{ Major = 2; Minor = 1 }
    Owner = "WsmProbe"
    ShouldTerminateOnLastHandleClosed = $true
    Container = [ordered]@{
      GuestOs = @{ HostName = "wsmprobe" }
      Storage = @{ Path = "C:\wsmprobe\plain" }
    }
  })
Add-Content $log "==== done ===="
