# Launch the capture agent + a visible GUI app in this container's own desktop
# using the token-borrow launcher (this path puts the child in the caller's
# session, which is the services session - a real desktop with a 1024x768 TSDDD
# framebuffer), so we can screenshot a container desktop now.
$ErrorActionPreference = 'Continue'

Write-Output '---- launch deskcap + regedt32 in the container desktop ----'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\wsm\session-run.ps1' `
    -AutoSession -Desktop 'winsta0\default' `
    -CommandLine 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Port 5901 -Log C:\wsm\deskcap-5901.log'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\wsm\session-run.ps1' `
    -AutoSession -Desktop 'winsta0\default' -Visible `
    -CommandLine 'C:\Windows\System32\regedt32.exe'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\wsm\session-run.ps1' `
    -AutoSession -Desktop 'winsta0\default' -Visible `
    -CommandLine 'C:\Windows\System32\cmd.exe /k echo container desktop'

Start-Sleep -Seconds 12
Write-Output '---- deskcap log ----'
(Get-Content C:\wsm\deskcap-5901.log -Raw -ErrorAction SilentlyContinue)
Write-Output '---- sessions ----'
(Get-Process | Group-Object SessionId | ForEach-Object {
        "  session {0}: {1} procs: {2}" -f $_.Name, $_.Count,
        (($_.Group | Select-Object -ExpandProperty ProcessName | Sort-Object -Unique) -join ',')
    }) -join "`n"
