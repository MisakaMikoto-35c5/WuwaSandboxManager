# Minimal check: do SYSTEM scheduled tasks run, and does Add-Type work as SYSTEM?
$ErrorActionPreference = 'Continue'

Set-Content -Path 'C:\wsm\min1.cmd' -Encoding ASCII -Value @'
whoami > C:\wsm\min1.txt 2>&1
whoami /priv >> C:\wsm\min1.txt 2>&1
'@

Set-Content -Path 'C:\wsm\min2.ps1' -Encoding ASCII -Value @'
whoami | Out-File C:\wsm\min2.txt
try {
    Add-Type -TypeDefinition 'public class TTT { public static int X() { return 7; } }'
    ('AddType=OK ' + [TTT]::X()) | Out-File C:\wsm\min2.txt -Append
}
catch { ('AddType=FAIL ' + $_.Exception.Message) | Out-File C:\wsm\min2.txt -Append }
'@

Remove-Item C:\wsm\min1.txt, C:\wsm\min2.txt -ErrorAction SilentlyContinue

& schtasks.exe /create /tn wsm-min1 /tr 'C:\wsm\min1.cmd' /sc once /st 23:59 /ru SYSTEM /f 2>&1 | Out-Null
& schtasks.exe /run /tn wsm-min1 2>&1 | Out-Null
& schtasks.exe /create /tn wsm-min2 /tr 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\min2.ps1' `
    /sc once /st 23:59 /ru SYSTEM /f 2>&1 | Out-Null
& schtasks.exe /run /tn wsm-min2 2>&1 | Out-Null

Start-Sleep -Seconds 25

Write-Output '---- min1.txt (SYSTEM cmd) ----'
(Get-Content C:\wsm\min1.txt -Raw -ErrorAction SilentlyContinue)
Write-Output '---- min2.txt (SYSTEM powershell + Add-Type) ----'
(Get-Content C:\wsm\min2.txt -Raw -ErrorAction SilentlyContinue)

Write-Output '---- task states ----'
& schtasks.exe /query /tn wsm-min1 /fo LIST 2>&1 | Select-String 'Status|Last Result' | ForEach-Object { 'min1 ' + $_.Line.Trim() }
& schtasks.exe /query /tn wsm-min2 /fo LIST 2>&1 | Select-String 'Status|Last Result' | ForEach-Object { 'min2 ' + $_.Line.Trim() }

& schtasks.exe /delete /tn wsm-min1 /f 2>&1 | Out-Null
& schtasks.exe /delete /tn wsm-min2 /f 2>&1 | Out-Null
