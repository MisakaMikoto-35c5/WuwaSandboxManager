$ErrorActionPreference='Continue'
$log = "$env:TEMP\opencode\wsmdiag\hive2.log"
"==== hive2 $(Get-Date -Format o) ====" | Set-Content $log
function L($t) { Add-Content $log $t }

L '--- sizes ---'
foreach ($f in 'C:\wsm-images\servercore-ltsc2022\Hives\SYSTEM','C:\wsm-images\servercore-ltsc2022\Files\Windows\System32\config\SYSTEM') {
  $i = Get-Item $f -ErrorAction SilentlyContinue
  if ($i) { L ("{0}  {1:N0} bytes  attrs={2}" -f $f, $i.Length, $i.Attributes) } else { L "$f (missing)" }
}

function ReadHive($hiveFile, $label) {
  L "######## $label"
  L "    file: $hiveFile"
  if (-not (Test-Path $hiveFile)) { L '    (missing)'; return }
  $copy = "$env:TEMP\opencode\wsmdiag\_h.bak"
  Copy-Item $hiveFile $copy -Force -ErrorAction SilentlyContinue
  $r = & reg.exe load 'HKLM\WSMH' $copy 2>&1
  L ("    load: " + ($r -join ' '))
  L '    root:'
  L ((& reg.exe query 'HKLM\WSMH' 2>&1) -join "`n")
  L '    Select:'
  L ((& reg.exe query 'HKLM\WSMH\Select' 2>&1) -join "`n")
  foreach ($cs in 'ControlSet001','ControlSet002') {
    $p = "HKLM\WSMH\$cs\Control\Session Manager"
    L "    --- $p ---"
    L ((& reg.exe query $p /v NumberOfInitialSessions 2>&1) -join "`n")
  }
  & reg.exe unload 'HKLM\WSMH' 2>&1 | Out-Null
}

ReadHive 'C:\wsm-images\servercore-ltsc2022\Files\Windows\System32\config\SYSTEM' 'servercore image: Files config SYSTEM'
ReadHive 'C:\wsm-images\servercore-ltsc2022\Hives\SYSTEM' 'servercore image: Hives SYSTEM'

L '######## locating a client image (has explorer.exe) among host container layers ########'
$found = 0
foreach ($d in (Get-ChildItem 'C:\ProgramData\Microsoft\Windows\Containers\Layers' -Directory -ErrorAction SilentlyContinue)) {
  $ex = Join-Path $d.FullName 'Files\Windows\System32\explorer.exe'
  if (Test-Path $ex) {
    $found++
    L ("CLIENT LAYER: {0}" -f $d.FullName)
    if ($found -le 2) {
      ReadHive (Join-Path $d.FullName 'Files\Windows\System32\config\SYSTEM') ("client layer SYSTEM: " + $d.Name)
    }
  }
}
L "client layers with explorer.exe: $found"
L '--- BaseImages ---'
Get-ChildItem 'C:\ProgramData\Microsoft\Windows\Containers\BaseImages' -Recurse -Depth 3 -ErrorAction SilentlyContinue | Select-Object -First 30 -ExpandProperty FullName | Add-Content $log
