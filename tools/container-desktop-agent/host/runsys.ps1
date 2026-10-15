# Runs C:\wsm\crossprobe.ps1 AS SYSTEM and returns its console output.
$ErrorActionPreference = 'Continue'
$out = 'C:\wsm\crossprobe.out'
Remove-Item $out -Force -ErrorAction SilentlyContinue

Set-Content -Path 'C:\wsm\crossprobe-run.cmd' -Encoding ASCII -Value @'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\crossprobe.ps1 > C:\wsm\crossprobe.out 2>&1
'@

& schtasks.exe /delete /tn wsm-cp /f 2>&1 | Out-Null
& schtasks.exe /create /tn wsm-cp /tr 'C:\wsm\crossprobe-run.cmd' /sc once /st 23:59 /ru SYSTEM /rl HIGHEST /f 2>&1 | Out-Null
& schtasks.exe /run /tn wsm-cp 2>&1 | Out-Null

for ($i = 0; $i -lt 120; $i++) {
    if ((Test-Path $out) -and (Get-Item $out).Length -gt 0) { Start-Sleep -Seconds 3; break }
    Start-Sleep -Milliseconds 500
}

Get-Content $out -Raw -ErrorAction SilentlyContinue
& schtasks.exe /delete /tn wsm-cp /f 2>&1 | Out-Null
