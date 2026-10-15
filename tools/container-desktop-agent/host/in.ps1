# Runs a local .ps1 INSIDE the container: base64-encode it (no quoting layers),
# then run the probe elevated (HCS needs admin) via a cmd redirect so the output
# is raw bytes (no mixed-encoding garbage).
param(
    [Parameter(Mandatory = $true)][string] $Script,
    [string] $Id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0e01',
    [string] $Out
)

$dir = "$env:TEMP\opencode\wsmdiag"
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"

$b64 = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes((Get-Content $Script -Raw)))
$cmdFile = "$dir\dd-in-cmd.txt"
Set-Content -Path $cmdFile -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand $b64" -Encoding ASCII

$action = "$dir\dd-in-action.ps1"
Set-Content -Path $action -Encoding ASCII -Value "& '$exe' shf $Id '$cmdFile'"

if (-not $Out) { $Out = "$dir\dd-in.out" }
Remove-Item $Out -ErrorAction SilentlyContinue
$wrapper = "$dir\dd-in.cmd"
Set-Content -Path $wrapper -Encoding ASCII -Value ('powershell -NoProfile -ExecutionPolicy Bypass -File "' + $action + '" > "' + $Out + '" 2>&1')

Start-Process cmd.exe -ArgumentList '/c', $wrapper -Verb RunAs -Wait
Get-Content $Out -Raw -ErrorAction SilentlyContinue
