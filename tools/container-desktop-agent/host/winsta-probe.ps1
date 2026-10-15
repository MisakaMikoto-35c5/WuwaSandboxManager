# winsta-probe.ps1 - runs ELEVATED (one UAC). It schedules the inner probe as
# SYSTEM (only SYSTEM can open another session's window station) and prints the result.
$ErrorActionPreference = 'Continue'
$dd = 'C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd'
$inner = Join-Path $dd 'winsta-probe-inner.ps1'
$out = Join-Path $dd 'winsta-probe.out'
$task = 'WSMWinstaProbe'

Remove-Item $out -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $dd 'winsta-*.png') -Force -ErrorAction SilentlyContinue

schtasks /delete /tn $task /f 2>&1 | Out-Null
$tr = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $inner + '"'
schtasks /create /tn $task /tr $tr /sc once /st 00:00 /ru SYSTEM /f 2>&1 | Select-Object -Last 1
schtasks /run /tn $task 2>&1 | Select-Object -Last 1

for ($i = 0; $i -lt 75; $i++) {
    Start-Sleep -Seconds 2
    if (Test-Path $out) {
        $c = Get-Content $out -Raw -ErrorAction SilentlyContinue
        if ($c -and $c -match '\[done\]') { break }
    }
}

Write-Output '===== winsta-probe.out ====='
Get-Content $out -ErrorAction SilentlyContinue
Write-Output ''
Write-Output '===== artifacts ====='
Get-ChildItem $dd -Filter 'winsta-*.png' -ErrorAction SilentlyContinue |
    Select-Object Name, Length | Format-Table -AutoSize
schtasks /delete /tn $task /f 2>&1 | Out-Null
