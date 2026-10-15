# Reads a file out of the container as base64 (no networking needed) and decodes it.
param(
    [string] $Remote = 'C:\wsm\shot.png',
    [string] $Out = "$env:TEMP\opencode\wsmdiag\dd\container-desktop.png"
)

$dd = "$env:TEMP\opencode\wsmdiag\dd"
$action = "$dd\pull-action.ps1"
Set-Content -Path $action -Encoding ASCII -Value @"
if (Test-Path '$Remote') { [Convert]::ToBase64String([IO.File]::ReadAllBytes('$Remote')) } else { 'MISSING' }
"@

$text = & "$dd\ps.ps1" -Script $action -TimeoutSec 180
$match = [regex]::Match($text, '[A-Za-z0-9+/=]{200,}')
if (-not $match.Success) {
    Write-Host 'no base64 payload found; raw output:'
    Write-Host $text
    exit 1
}

$bytes = [Convert]::FromBase64String($match.Value)
[IO.File]::WriteAllBytes($Out, $bytes)
Write-Host ("saved {0:N0} bytes to {1}" -f $bytes.Length, $Out)
