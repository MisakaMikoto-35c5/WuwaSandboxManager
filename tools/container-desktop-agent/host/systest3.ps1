# Read the previous SYSTEM-hop result, then time a single launch.
$ErrorActionPreference = 'Continue'
$sw = [Diagnostics.Stopwatch]::StartNew()

Write-Output '---- previous sys-run.result ----'
(Get-Content 'C:\wsm\sys-run.result' -Raw -ErrorAction SilentlyContinue)

Write-Output ('---- session-run.dll present: ' + (Test-Path 'C:\wsm\SessionLauncher.dll'))
if (Test-Path 'C:\wsm\SessionLauncher.dll') {
    Write-Output ('     ' + (Get-Item 'C:\wsm\SessionLauncher.dll').Length + ' bytes, ' + (Get-Item 'C:\wsm\SessionLauncher.dll').LastWriteTime)
}

Write-Output '---- one launch through the SYSTEM hop ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\wsm\session-run-as-system.ps1' `
    -Desktop 'winsta0\default' -CommandLine 'C:\Windows\System32\cmd.exe' -Visible
Write-Output ('elapsed seconds: ' + [Math]::Round($sw.Elapsed.TotalSeconds, 1))

Write-Output ('---- dll after: ' + (Test-Path 'C:\wsm\SessionLauncher.dll'))
Write-Output '---- result file after ----'
(Get-Content 'C:\wsm\sys-run.result' -Raw -ErrorAction SilentlyContinue)

Write-Output '---- sessions ----'
(Get-Process | Group-Object SessionId | ForEach-Object {
        "session {0}: {1} procs: {2}" -f $_.Name, $_.Count,
        (($_.Group | Select-Object -ExpandProperty ProcessName | Sort-Object -Unique) -join ',')
    }) -join "`n"
