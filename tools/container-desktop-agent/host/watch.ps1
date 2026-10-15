# Watch the console session come and go during a boot.
$ErrorActionPreference = 'Continue'
$me = (Get-Process -Id $PID).SessionId
for ($i = 0; $i -lt 21; $i++) {
    $time = (Get-Date).ToString('HH:mm:ss')
    $sessions = ((qwinsta 2>&1 | Out-String) -replace "`r?`n", ' / ').Trim()
    $other = ((Get-Process | Where-Object { $_.SessionId -ne 0 -and $_.SessionId -ne $me } |
            ForEach-Object { $_.ProcessName + '@' + $_.SessionId }) -join ',')
    Write-Output ("$time  qwinsta: $sessions")
    Write-Output ("        other-session procs: $other")
    Write-Output ("        proof=" + (Test-Path C:\wsm\proof-shell.txt) + " deskcapLog=" + (Test-Path C:\wsm\deskcap-5901.log))
    Start-Sleep -Seconds 10
}
Write-Output '---- proof file ----'
(Get-Content C:\wsm\proof-shell.txt -Raw -ErrorAction SilentlyContinue)
Write-Output '---- deskcap log ----'
(Get-Content C:\wsm\deskcap-5901.log -Raw -ErrorAction SilentlyContinue)
Write-Output '---- LocalSessionManager log ----'
(Get-WinEvent -LogName 'Microsoft-Windows-TerminalServices-LocalSessionManager/Operational' -MaxEvents 12 -ErrorAction SilentlyContinue |
    ForEach-Object { '  ' + $_.TimeCreated.ToString('HH:mm:ss') + ' #' + $_.Id + ' ' + (($_.Message -replace "`r?`n", ' ')) }) -join "`n"
