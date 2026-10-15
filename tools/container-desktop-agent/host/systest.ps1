# Does a SYSTEM scheduled task work in this container, and does the launcher then
# reach the console session?
$ErrorActionPreference = 'Continue'
$dir = 'C:\wsm'
$argumentFile = "$dir\sys-run.args"
$resultFile = "$dir\sys-run.result"

Set-Content -Path $argumentFile -Encoding ASCII -Value @(
    'Desktop=winsta0\default'
    'CommandLine=C:\Windows\System32\cmd.exe'
    'Visible=1'
)
Set-Content -Path "$dir\sys-run.ps1" -Encoding ASCII -Value @"
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -ArgumentFile $argumentFile *> $resultFile
"@
Remove-Item $resultFile -ErrorAction SilentlyContinue

Write-Output '---- identity in this (container admin) process ----'
whoami
whoami /priv | Select-String 'Debug|AssignPrimary|Tcb|IncreaseQuota' | Out-String

Write-Output '---- schtasks /create as SYSTEM ----'
& schtasks.exe /create /tn wsm-test /tr "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\sys-run.ps1" `
    /sc once /st 23:59 /ru SYSTEM /rl HIGHEST /f 2>&1 | Out-String
Write-Output ("create exit=" + $LASTEXITCODE)

Write-Output '---- schtasks /run ----'
& schtasks.exe /run /tn wsm-test 2>&1 | Out-String
Write-Output ("run exit=" + $LASTEXITCODE)

for ($i = 0; $i -lt 40 -and -not (Test-Path $resultFile); $i++) { Start-Sleep -Milliseconds 500 }
Write-Output ('result file present: ' + (Test-Path $resultFile))

Write-Output '---- task last result ----'
& schtasks.exe /query /tn wsm-test /v /fo LIST 2>&1 |
    Select-String -Pattern 'Run As User|Last Run Time|Last Result|Status|Task To Run' | ForEach-Object { $_.Line.Trim() }

Write-Output '---- launcher result ----'
(Get-Content $resultFile -Raw -ErrorAction SilentlyContinue)

Write-Output '---- sessions now ----'
(qwinsta 2>&1 | Out-String)
(Get-Process | Group-Object SessionId | ForEach-Object {
        "session {0}: {1} procs: {2}" -f $_.Name, $_.Count,
        (($_.Group | Select-Object -ExpandProperty ProcessName | Sort-Object -Unique) -join ',')
    }) -join "`n"

& schtasks.exe /delete /tn wsm-test /f 2>&1 | Out-Null
