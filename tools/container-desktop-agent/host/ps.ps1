# Runs a local .ps1 inside the container through the host runner's queue.
param(
    [Parameter(Mandatory = $true)][string] $Script,
    [int] $TimeoutSec = 300,
    [string] $Out
)

$b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("`$ProgressPreference = 'SilentlyContinue'`n" + (Get-Content $Script -Raw)))
$cmdline = "powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand $b64"
$text = & "$env:TEMP\opencode\wsmdiag\dd\q.ps1" -Line $cmdline -TimeoutSec $TimeoutSec
if ($Out) { $text | Set-Content $Out }
$text
