Remove-Item C:\wsm\shot2.png, C:\wsm\dc2.log -Force -ErrorAction SilentlyContinue
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\shot2.png -Log C:\wsm\dc2.log 2>&1 | Out-String
Write-Output ('shot2: ' + (Test-Path C:\wsm\shot2.png))
Write-Output ('dc2 log: ' + (Test-Path C:\wsm\dc2.log))
(Get-Content C:\wsm\dc2.log -Raw -ErrorAction SilentlyContinue)
