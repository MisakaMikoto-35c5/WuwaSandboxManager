$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\env.log"
"==== env $(Get-Date -Format o) ====" | Set-Content $log
$exe = 'C:\Users\User\Documents\WindowsSandboxManager\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe'

Remove-Item Env:\WSM_CONTAINER_BASE_IMAGE -ErrorAction SilentlyContinue
Add-Content $log "== no image configured =="
& $exe env *>&1 | Add-Content $log

$env:WSM_CONTAINER_BASE_IMAGE = 'C:\wsm-images\servercore-ltsc2022'
Add-Content $log "== with Windows Server image =="
& $exe env *>&1 | Add-Content $log
Add-Content $log "== done =="
