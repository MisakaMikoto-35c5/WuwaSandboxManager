$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\exp7.log"
"==== exp7 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$base = "C:\ProgramData\Microsoft\Windows\Containers\Layers\cf0776b2-3566-4743-90e5-6f0db1f2064c"
$scratch = "C:\wsmprobe\cs40"
Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
& $exe variants "unused" $scratch $base 2>&1 | Add-Content $log
Add-Content $log "==== done ===="
