$ErrorActionPreference = 'Continue'
$task = 'wsm-session-run'

Set-Content -Path 'C:\wsm\sys-run.ps1' -Encoding ASCII -Value @'
$text = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -ArgumentFile C:\wsm\sys-run.args 2>&1 | Out-String
Set-Content -Path C:\wsm\sys-run.result -Value $text
'@
Set-Content -Path 'C:\wsm\sys-run.args' -Encoding ASCII -Value @(
    'Desktop=winsta0\default'
    'CommandLine=C:\Windows\System32\cmd.exe'
    'Visible=1'
)
Remove-Item 'C:\wsm\sys-run.result' -Force -ErrorAction SilentlyContinue

function Show($label, $output, $code) {
    Write-Output ("-- $label exit=$code")
    $output | ForEach-Object { '   ' + $_ }
}

$o = & schtasks.exe /delete /tn $task /f 2>&1; Show 'delete' $o $LASTEXITCODE
$o = & schtasks.exe /query /tn $task 2>&1; Show 'query-before' $o $LASTEXITCODE
$o = & schtasks.exe /create /tn $task /tr "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\sys-run.ps1" `
    /sc once /st 23:59 /ru SYSTEM /rl HIGHEST /f 2>&1; Show 'create' $o $LASTEXITCODE
$o = & schtasks.exe /query /tn $task /fo LIST /v 2>&1 |
    Select-String 'TaskName|Status|Run As User|Task To Run|Last Result'; Show 'query-after' $o 0
$o = & schtasks.exe /run /tn $task 2>&1; Show 'run' $o $LASTEXITCODE

for ($i = 0; $i -lt 60; $i++) {
    if ((Test-Path 'C:\wsm\sys-run.result') -and (Get-Item 'C:\wsm\sys-run.result').Length -gt 0) { break }
    Start-Sleep -Milliseconds 500
}
Write-Output ('-- result present: ' + (Test-Path 'C:\wsm\sys-run.result') + ' size: ' + (Get-Item 'C:\wsm\sys-run.result' -ErrorAction SilentlyContinue).Length)
Get-Content 'C:\wsm\sys-run.result' -Raw -ErrorAction SilentlyContinue

Write-Output '-- sessions'
(Get-Process | Group-Object SessionId | ForEach-Object {
        "   session {0}: {1} procs: {2}" -f $_.Name, $_.Count,
        (($_.Group | Select-Object -ExpandProperty ProcessName | Sort-Object -Unique) -join ',')
    }) -join "`n"

$o = & schtasks.exe /delete /tn $task /f 2>&1; Show 'delete-after' $o $LASTEXITCODE
