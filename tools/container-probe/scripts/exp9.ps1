$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\exp9.log"
"==== exp9 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base = "C:\ProgramData\Microsoft\Windows\Containers\Layers\cf0776b2-3566-4743-90e5-6f0db1f2064c"
Remove-Item -Recurse -Force C:\wsmprobe\deep -ErrorAction SilentlyContinue
& $exe deep C:\wsmprobe\deep $base 2>&1 | Add-Content $log
Add-Content $log "== recent HCS/container events =="
Get-WinEvent -LogName 'Microsoft-Windows-Hyper-V-Compute-Admin' -MaxEvents 8 -ErrorAction SilentlyContinue | Select-Object TimeCreated,Id,LevelDisplayName,Message | Format-List | Out-String | Add-Content $log
Get-WinEvent -ListLog '*Compute*' -ErrorAction SilentlyContinue | Select-Object LogName,RecordCount | Out-String | Add-Content $log
Add-Content $log "==== done ===="
