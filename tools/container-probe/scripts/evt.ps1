$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\evt.log"
"==== events $(Get-Date -Format o) ====" | Set-Content $log
foreach ($ln in @('Microsoft-Windows-Hyper-V-Compute-Admin','Microsoft-Windows-Hyper-V-Compute-Operational')) {
  Add-Content $log "########## $ln ##########"
  Get-WinEvent -LogName $ln -MaxEvents 25 -ErrorAction SilentlyContinue |
    Sort-Object TimeCreated |
    ForEach-Object { "--- $($_.TimeCreated.ToString('HH:mm:ss.fff')) id=$($_.Id) lvl=$($_.LevelDisplayName)`n$($_.Message)" } |
    Add-Content $log
}
Add-Content $log "==== done ===="
