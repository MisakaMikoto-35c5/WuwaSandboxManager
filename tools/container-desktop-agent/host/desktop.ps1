# Container-side: launch things on the console session's desktops and capture.
$ErrorActionPreference = 'Continue'

function Launch([string] $desktop, [string] $cmdline, [bool] $visible) {
    Write-Output ("---- launch [$desktop] visible=$visible : $cmdline")
    $params = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'C:\wsm\session-run-as-system.ps1',
        '-Desktop', $desktop, '-CommandLine', $cmdline)
    if ($visible) { $params += '-Visible' }
    & powershell.exe @params 2>&1
}

# Screenshot servers: one on the interactive desktop, one on the secure logon desktop.
Launch 'winsta0\default' 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Port 5901 -Log C:\wsm\deskcap-5901.log' $false
Launch 'winsta0\Winlogon' 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Port 5902 -Log C:\wsm\deskcap-5902.log' $false

# Visible proof: a console and the registry editor on the interactive desktop.
Launch 'winsta0\default' 'C:\Windows\System32\cmd.exe' $true
Launch 'winsta0\default' 'C:\Windows\System32\regedt32.exe' $true

Start-Sleep -Seconds 8

Write-Output ''
Write-Output '---- processes by session ----'
(Get-Process | Group-Object SessionId | ForEach-Object {
        "session {0}: {1} procs: {2}" -f $_.Name, $_.Count,
        (($_.Group | Select-Object -ExpandProperty ProcessName | Sort-Object -Unique) -join ',')
    }) -join "`n"

Write-Output ''
Write-Output '---- deskcap logs ----'
(Get-Content C:\wsm\deskcap-5901.log -ErrorAction SilentlyContinue) -join "`n"
(Get-Content C:\wsm\deskcap-5902.log -ErrorAction SilentlyContinue) -join "`n"
