# run-assystem.ps1 (HOST, runs ELEVATED) - demonstrate "a SYSTEM process shows a
# window on the user's console": create a one-shot scheduled task that runs as
# SYSTEM, let it launch the container-desktop viewer into the interactive console
# session (via launch-in-console.ps1 -> WTSQueryUserToken + CreateProcessAsUser),
# then remove the task again.
$tn = 'WSMShowConsole'
$dd = 'C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd'

Write-Output "elevated identity: $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
& schtasks.exe /delete /f /tn $tn 2>$null | Out-Null
Write-Output '--- create the SYSTEM task ---'
& schtasks.exe /create /f /tn $tn /sc once /st 23:59 /ru SYSTEM /rl HIGHEST /tr "cmd.exe /c $dd\asm.cmd"
Write-Output '--- run it ---'
& schtasks.exe /run /tn $tn
Start-Sleep -Seconds 14
Write-Output '--- remove the task ---'
& schtasks.exe /delete /f /tn $tn
Write-Output '--- launch.log (written by the SYSTEM process) ---'
Get-Content "$dd\launch.log" -ErrorAction SilentlyContinue
