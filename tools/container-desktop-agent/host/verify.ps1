# Container-side: verify the agent landed and that the container is healthy.
$ErrorActionPreference = 'Continue'

Write-Output '---- C:\wsm ----'
Get-ChildItem C:\wsm -ErrorAction SilentlyContinue | Select-Object Name, Length, LastWriteTime |
    Format-Table -AutoSize | Out-String

Write-Output '---- scripts parse ----'
foreach ($f in 'C:\wsm\session-run.ps1', 'C:\wsm\deskcap.ps1') {
    if (Test-Path $f) {
        try {
            $null = [ScriptBlock]::Create((Get-Content $f -Raw))
            Write-Output ("{0}: parses OK ({1} bytes)" -f $f, (Get-Item $f).Length)
        }
        catch { Write-Output ("{0}: PARSE FAIL {1}" -f $f, $_.Exception.Message) }
    }
    else { Write-Output ("{0}: MISSING" -f $f) }
}

Write-Output '---- sessions ----'
(qwinsta 2>&1 | Out-String)

Write-Output '---- session 5 (console) processes ----'
(Get-Process | Where-Object { $_.SessionId -ne 0 -and $_.SessionId -ne (Get-Process -Id $PID).SessionId } |
    ForEach-Object { "{0}#{1}" -f $_.ProcessName, $_.Id }) -join ', '
