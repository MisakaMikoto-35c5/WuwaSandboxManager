# Runs a host-side script elevated and returns its output.
param(
    [Parameter(Mandatory = $true)][string] $Script
)

$dir = "$env:TEMP\opencode\wsmdiag"
$out = "$dir\dd\elev.out"
Remove-Item $out -ErrorAction SilentlyContinue

$wrapper = "$dir\dd\elev.cmd"
Set-Content -Path $wrapper -Encoding ASCII -Value ('powershell -NoProfile -ExecutionPolicy Bypass -File "' + $Script + '" > "' + $out + '" 2>&1')
Start-Process cmd.exe -ArgumentList '/c', $wrapper -Verb RunAs -Wait
Get-Content $out -Raw -ErrorAction SilentlyContinue
