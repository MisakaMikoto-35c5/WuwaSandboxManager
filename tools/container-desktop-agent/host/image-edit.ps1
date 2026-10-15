# Host-side, elevated: install the boot-time agent into the image layer and check
# whether the container's registry writes persisted into the image's hives.
$ErrorActionPreference = 'Continue'

$dir = "$env:TEMP\opencode\wsmdiag"
$image = 'C:\wsm-images\servercore-ltsc2022'
$repo = 'C:\Users\User\Documents\WindowsSandboxManager\tools\container-desktop-agent'
$target = "$image\Files\wsm"

Write-Output '---- running compute systems (should be none) ----'
try {
    ((& hcsdiag.exe list -raw 2>&1) -join "`n").Substring(0, 200)
}
catch { 'hcsdiag: ' + $_.Exception.Message }
try { Get-Process hcsprobe -ErrorAction SilentlyContinue | Select-Object Id, StartTime | Format-Table -AutoSize | Out-String } catch { }

Write-Output '---- install the agent into the image layer ----'
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item "$repo\deskcap.ps1" "$target\deskcap.ps1" -Force
Copy-Item "$repo\agent.cmd" "$target\agent.cmd" -Force
Get-ChildItem $target | Select-Object Name, Length | Format-Table -AutoSize | Out-String

Write-Output '---- did the container registry writes persist into the image SOFTWARE hive? ----'
$software = "$image\Files\Windows\System32\config\SOFTWARE"
& reg.exe unload HKLM\WSMCHK 2>&1 | Out-Null
$load = (& reg.exe load HKLM\WSMCHK $software 2>&1) -join ' '
Write-Output ('load: ' + $load)
if ($load -notmatch 'ERROR') {
    (& reg.exe query 'HKLM\WSMCHK\Microsoft\Windows NT\CurrentVersion\Winlogon' 2>&1 |
        Select-String 'AutoAdminLogon|DefaultUser|DefaultDomain|DefaultPass|ForceAutoLogon|Shell|WsmProbeMarker|UIHost' |
        ForEach-Object { $_.Line.Trim() }) -join "`n"
    & reg.exe unload HKLM\WSMCHK 2>&1 | Out-Null
}

Write-Output '---- host reference for the UIHost hook ----'
(& reg.exe query 'HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' /v UIHost 2>&1) -join "`n"

Write-Output '---- image file hashes (for the record) ----'
foreach ($f in 'SYSTEM', 'SOFTWARE', 'SAM') {
    $p = "$image\Files\Windows\System32\config\$f"
    if (Test-Path $p) { Write-Output ("  {0}: {1} ({2:N0} bytes)" -f $f, (Get-FileHash $p -Algorithm SHA256).Hash.Substring(0, 16), (Get-Item $p).Length) }
}
