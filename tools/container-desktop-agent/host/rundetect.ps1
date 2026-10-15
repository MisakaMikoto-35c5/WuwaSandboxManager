# Fresh container: is the image layer's C:\wsm visible? Then run the logon probe as SYSTEM.
$ErrorActionPreference = 'Continue'

Write-Output '---- C:\wsm (is the image layer visible?) ----'
(Get-ChildItem C:\wsm -ErrorAction SilentlyContinue | Select-Object Name, Length | Format-Table -AutoSize | Out-String)

Write-Output '---- run logontest.ps1 as SYSTEM ----'
Remove-Item C:\wsm\logontest.out -Force -ErrorAction SilentlyContinue
Set-Content -Path 'C:\wsm\logontest-run.cmd' -Encoding ASCII -Value @'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\logontest.ps1 > C:\wsm\logontest.out 2>&1
'@

& schtasks.exe /delete /tn wsm-lt /f 2>&1 | Out-Null
& schtasks.exe /create /tn wsm-lt /tr 'C:\wsm\logontest-run.cmd' /sc once /st 23:59 /ru SYSTEM /rl HIGHEST /f 2>&1 | Out-Null
& schtasks.exe /run /tn wsm-lt 2>&1 | Out-Null

for ($i = 0; $i -lt 120; $i++) {
    if ((Test-Path 'C:\wsm\logontest.out') -and (Get-Item 'C:\wsm\logontest.out').Length -gt 0) { Start-Sleep -Seconds 3; break }
    Start-Sleep -Milliseconds 500
}
(Get-Content 'C:\wsm\logontest.out' -Raw -ErrorAction SilentlyContinue)
& schtasks.exe /delete /tn wsm-lt /f 2>&1 | Out-Null
