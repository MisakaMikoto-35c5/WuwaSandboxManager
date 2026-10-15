$ErrorActionPreference='Continue'
$log = "$env:TEMP\opencode\wsmdiag\hive.log"
"==== hive $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }

L '--- hardlink identity: Hives\SYSTEM vs Files\...\config\SYSTEM ---'
L ((& fsutil.exe hardlink list 'C:\wsm-images\servercore-ltsc2022\Hives\SYSTEM' 2>&1) -join "`n")

function QueryHive($hiveFile, $label) {
  L "######## $label : $hiveFile ########"
  if (-not (Test-Path $hiveFile)) { L '  (missing)'; return }
  & reg.exe unload 'HKLM\WSMIMG' 2>&1 | Out-Null
  L ((& reg.exe load 'HKLM\WSMIMG' $hiveFile 2>&1) -join "`n")
  L '--- root subkeys ---'
  L ((& reg.exe query 'HKLM\WSMIMG' 2>&1) -join "`n")
  L '--- Select ---'
  L ((& reg.exe query 'HKLM\WSMIMG\Select' 2>&1) -join "`n")
  foreach ($cs in 'ControlSet001','ControlSet002','CurrentControlSet') {
    foreach ($sub in 'Control\Session Manager','Control\ProductOptions') {
      $p = "HKLM\WSMIMG\$cs\$sub"
      L "--- $p ---"
      L ((& reg.exe query $p 2>&1) -join "`n")
      if ($sub -like '*Session Manager*') {
        L "--- $p\SubSystems ---"
        L ((& reg.exe query "$p\SubSystems" 2>&1) -join "`n")
      }
    }
  }
  & reg.exe unload 'HKLM\WSMIMG' 2>&1 | Out-Null
}

QueryHive 'C:\wsm-images\servercore-ltsc2022\Hives\SYSTEM' 'servercore-ltsc2022 (WS2022 servercore)'
QueryHive 'C:\wsmprobe\img\base1\Hives\SYSTEM' 'base1'

L '--- find Windows Sandbox client image layers ---'
Get-ChildItem 'C:\ProgramData\Microsoft\Windows\Containers' -Recurse -Depth 3 -Directory -ErrorAction SilentlyContinue | Select-Object -First 40 -ExpandProperty FullName | Add-Content $log
Get-ChildItem 'C:\ProgramData\Microsoft\Windows\Containers' -Recurse -Depth 4 -Filter 'SYSTEM' -File -ErrorAction SilentlyContinue | Select-Object -First 20 -ExpandProperty FullName | Add-Content $log
