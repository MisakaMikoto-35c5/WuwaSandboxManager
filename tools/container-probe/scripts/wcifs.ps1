$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\wcifs.log"
"==== wcifs/bindflt events $(Get-Date -Format o) ====" | Set-Content $log
foreach ($ln in @('Microsoft-Windows-Containers-Wcifs/Operational','Microsoft-Windows-Containers-BindFlt/Operational')) {
  Add-Content $log "########## $ln ##########"
  Get-WinEvent -LogName $ln -MaxEvents 40 -ErrorAction SilentlyContinue |
    Sort-Object TimeCreated |
    ForEach-Object { "--- $($_.TimeCreated.ToString('HH:mm:ss.fff')) id=$($_.Id) lvl=$($_.LevelDisplayName)`n$($_.Message)" } |
    Add-Content $log
}
Add-Content $log "==== done ===="
