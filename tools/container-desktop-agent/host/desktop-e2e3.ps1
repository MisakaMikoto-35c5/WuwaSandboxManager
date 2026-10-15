$ErrorActionPreference = 'Continue'
$dd = "$env:TEMP\opencode\wsmdiag\dd"
$wsm = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'
$state = Get-Content "$dd\state.json" -Raw | ConvertFrom-Json
$cs = $state.id
$root = $state.mount
Write-Output "compute-system = $cs"
Write-Output "container C:\  = $root"

Write-Output ''
Write-Output '========== 1. start the desktop channel and present it in the interactive session =========='
& $wsm desktop --compute-system $cs --container-root $root --attach
Write-Output "exit=$LASTEXITCODE"

Write-Output ''
Write-Output '========== 2. second invocation: adopt the running agent, then send input =========='
& $wsm desktop --compute-system $cs --container-root $root --input "keys F3"
Write-Output "exit=$LASTEXITCODE"
Start-Sleep -Seconds 4

Write-Output ''
Write-Output '========== 3. the agent log inside the container (read straight from the host) =========='
Get-Content "$root\wsm\agent.log" -Tail 14 -ErrorAction SilentlyContinue

Write-Output ''
Write-Output '========== 4. shared frame buffer =========='
Get-Item "$root\wsm\frame.shm" -ErrorAction SilentlyContinue |
    Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize

Write-Output ''
Write-Output '========== 5. viewer window process (session id matters) =========='
Get-Process wsm -ErrorAction SilentlyContinue |
    Select-Object Id, SessionId, MainWindowTitle | Format-Table -AutoSize

Write-Output ''
Write-Output '========== 6. stop the channel =========='
& $wsm desktop --compute-system $cs --container-root $root --stop
Start-Sleep -Seconds 6
Get-Content "$root\wsm\agent.log" -Tail 3 -ErrorAction SilentlyContinue

Write-Output ''
Write-Output '========== 7. close the test viewer =========='
Get-Process wsm -ErrorAction SilentlyContinue |
    Where-Object { $_.MainWindowTitle -like 'WSM - container desktop*' } |
    ForEach-Object { Write-Output ("closing pid " + $_.Id); Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
