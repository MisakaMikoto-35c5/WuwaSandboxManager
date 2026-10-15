$ErrorActionPreference = 'Continue'
$log = "$env:TEMP\opencode\wsmdiag\exp4.log"
"==== exp4 $(Get-Date -Format o) ====" | Set-Content $log
$exe = "C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$top = "C:\ProgramData\Microsoft\Windows\Containers\Layers\6a50e60e-b4b0-45cc-abc7-87d84f6551ea"
$base = "C:\ProgramData\Microsoft\Windows\Containers\Layers\cf0776b2-3566-4743-90e5-6f0db1f2064c"

function Run([string]$desc, [string[]]$argv) {
  Add-Content $log "== $desc =="
  & $exe $argv 2>&1 | Add-Content $log
}

Run "lexists top" @('lexists', $top)
Run "lexists base" @('lexists', $base)
Run "lactivate base" @('lactivate', $base)
Run "lactivate top" @('lactivate', $top)
Run "lmount base" @('lmount', $base)
Run "lmount top" @('lmount', $top)

Remove-Item -Recurse -Force C:\wsmprobe\l2 -ErrorAction SilentlyContinue
Run "lcreate (readonly layer) from top" @('lcreate', 'C:\wsmprobe\l2', $top)

Remove-Item -Recurse -Force C:\wsmprobe\cs3 -ErrorAction SilentlyContinue
Run "lsandbox mkdir from base" @('lsandbox', 'C:\wsmprobe\cs3', $base)

Remove-Item -Recurse -Force C:\wsmprobe\cs4 -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force C:\wsmprobe\cs4 | Out-Null
Run "lsandbox preexisting from base" @('lsandbox', 'C:\wsmprobe\cs4', $base)

Remove-Item -Recurse -Force C:\wsmprobe\cs5 -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force C:\wsmprobe\cs5 | Out-Null
Run "lsandbox preexisting from top" @('lsandbox', 'C:\wsmprobe\cs5', $top)

Run "lexists cs5" @('lexists', 'C:\wsmprobe\cs5')
Run "lmount cs5" @('lmount', 'C:\wsmprobe\cs5')
Add-Content $log "==== done ===="
