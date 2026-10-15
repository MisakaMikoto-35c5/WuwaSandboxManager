$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\exp10.log"
"==== exp10 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base = "C:\ProgramData\Microsoft\Windows\Containers\Layers\cf0776b2-3566-4743-90e5-6f0db1f2064c"
$dir = "$env:TEMP\opencode\wsmdiag"

function Try-C1([string]$name, $obj, [string]$cmd = "cmd.exe /c whoami && ver") {
  $json = $obj | ConvertTo-Json -Depth 12 -Compress
  $f = "$dir\c1_$name.json"
  Set-Content -Path $f -Value $json -Encoding UTF8
  $id = "wsmprobe-$name"
  Add-Content $log "===== $name ====="
  Add-Content $log $json
  $createOut = & $exe create $id $f 2>&1
  Add-Content $log ($createOut -join "`n")
  if ($createOut -match 'CREATED OK') {
    $startOut = & $exe start $id 2>&1
    Add-Content $log ($startOut -join "`n")
    if ($startOut -match 'STARTED OK') {
      Start-Sleep -Seconds 5
      $execOut = & $exe exec $id $cmd 2>&1
      Add-Content $log "EXEC: " + ($execOut -join "`n")
    }
    & $exe stop $id 2>&1 | Add-Content $log
  }
  Start-Sleep -Milliseconds 500
}

$nameGuid = (& $exe lname $base).Trim()
Add-Content $log "nameGuid=$nameGuid"

# scratch with sandbox.vhdx
$scratch = "C:\wsmprobe\s1"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe lsandbox $scratch $base 2>&1 | Add-Content $log

# A: schema1 with LayerFolderPath (folder) + name guid
Try-C1 "s1_folder_name" ([ordered]@{
  SystemType = "Container"; Name = "wsmprobe"; Owner = "WsmProbe"
  LayerFolderPath = $scratch
  Layers = @(@{ Id = $nameGuid; Path = $base })
  HvPartition = $false
  TerminateOnLastHandleClosed = $true
})

# B: schema1 with registered guid
Try-C1 "s1_folder_reg" ([ordered]@{
  SystemType = "Container"; Name = "wsmprobe"; Owner = "WsmProbe"
  LayerFolderPath = $scratch
  Layers = @(@{ Id = "cf0776b2-3566-4743-90e5-6f0db1f2064c"; Path = $base })
  HvPartition = $false
  TerminateOnLastHandleClosed = $true
})

# C: schema1 with LayerFolderPath = Files dir
Try-C1 "s1_folderfiles_name" ([ordered]@{
  SystemType = "Container"; Name = "wsmprobe"; Owner = "WsmProbe"
  LayerFolderPath = "$scratch\Files"
  Layers = @(@{ Id = $nameGuid; Path = $base })
  HvPartition = $false
  TerminateOnLastHandleClosed = $true
})

# D: schema1 with no LayerFolderPath, VolumePath = volume
$vol = (& $exe lmount $scratch).Trim().TrimEnd('\')
Try-C1 "s1_volume_name" ([ordered]@{
  SystemType = "Container"; Name = "wsmprobe"; Owner = "WsmProbe"
  VolumePath = $vol
  Layers = @(@{ Id = $nameGuid; Path = $base })
  HvPartition = $false
  TerminateOnLastHandleClosed = $true
})

Add-Content $log "==== done ===="
