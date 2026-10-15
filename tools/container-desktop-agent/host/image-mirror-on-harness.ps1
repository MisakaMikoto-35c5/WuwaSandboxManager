# image-mirror-on-harness.ps1 - runs ELEVATED.
# 1. stop the harness container (releases the image layer)
# 2. patch the image the harness uses: console session + window-mirror payload
#    (plus a shell and auto-logon, so the container actually logs on and our read side
#    - which is started at logon - runs inside its session)
# 3. restart the container and look for the read side's output
$ErrorActionPreference = 'Continue'
$dd = 'C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd'
$wsm = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$layer = 'C:\wsm-images\servercore-ltsc2022'
$root = (Get-Content (Join-Path $dd 'state.json') -Raw | ConvertFrom-Json).mount
$wsmDir = [System.IO.Path]::Combine($root, 'wsm')

Write-Output '===== 1. stop the container ====='
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dd 'q.ps1') -Line '@stop' -TimeoutSec 120 2>&1 | Select-Object -Last 3
Start-Sleep -Seconds 3

Write-Output ''
Write-Output '===== 2. patch the image ====='
& $wsm image desktop $layer
& $wsm image mirror $layer --shell cmd.exe --logon-user ContainerAdministrator --logon-password ''

Write-Output ''
Write-Output '===== 3. restart the container ====='
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dd 'run-host.ps1') 2>&1 | Select-Object -Last 4
$state = Get-Content (Join-Path $dd 'state.json') -Raw | ConvertFrom-Json
$wsmDir = [System.IO.Path]::Combine($state.mount, 'wsm')
Write-Output ("container root = " + $state.mount)

Write-Output ''
Write-Output '===== 4. did the container''s logon start the read side? ====='
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Seconds 3
    if ([System.IO.File]::Exists([System.IO.Path]::Combine($wsmDir, 'mirror.shm'))) { break }
}
foreach ($f in @('mirror.shm', 'read.log', 'read.out', 'wsm-desktop-read.ps1', 'wsm-winlogon-wrap.cmd')) {
    $p = [System.IO.Path]::Combine($wsmDir, $f)
    $size = if ([System.IO.File]::Exists($p)) { ([System.IO.FileInfo]::new($p)).Length } else { -1 }
    Write-Output ("  " + $(if ($size -ge 0) { "[x] $f ($size bytes)" } else { "[ ] $f" }))
}
$log = [System.IO.Path]::Combine($wsmDir, 'read.log')
if ([System.IO.File]::Exists($log)) {
    Write-Output '  --- read.log ---'
    Get-Content $log -Tail 12 | ForEach-Object { Write-Output ("    " + $_) }
}
$out = [System.IO.Path]::Combine($wsmDir, 'read.out')
if ([System.IO.File]::Exists($out)) {
    Write-Output '  --- read.out (last lines) ---'
    Get-Content $out -Tail 8 | ForEach-Object { Write-Output ("    " + $_) }
}

Write-Output ''
Write-Output '===== 5. sessions / processes in the container (host view) ====='
Get-Process | Where-Object { $_.SessionId -in 4, 5 } |
    Group-Object SessionId, ProcessName | Select-Object Name, Count | Format-Table -AutoSize
