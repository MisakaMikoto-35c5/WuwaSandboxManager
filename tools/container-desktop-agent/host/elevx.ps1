# elevx.ps1 - like elev.ps1, but every call has its OWN output file, so concurrent or
# previous elevated runs cannot be confused with this one (elev.out was contended).
param(
    [Parameter(Mandatory = $true)][string] $Script,
    [Parameter(Mandatory = $false)][string] $Out
)

if (-not $Out) { $Out = [System.IO.Path]::Combine([System.IO.Path]::GetDirectoryName($Script), ([System.IO.Path]::GetFileNameWithoutExtension($Script) + '.out')) }
if (Test-Path $Out) { Remove-Item $Out -Force -ErrorAction SilentlyContinue }

$wrapper = [System.IO.Path]::ChangeExtension($Out, '.cmd')
Set-Content -Path $wrapper -Encoding ASCII -Value ('powershell -NoProfile -ExecutionPolicy Bypass -File "' + $Script + '" > "' + $Out + '" 2>&1')

Write-Output ("[elevx] launching elevated: " + $Script)
Write-Output ("[elevx] output goes to     : " + $Out)
Start-Process cmd.exe -ArgumentList '/c', $wrapper -Verb RunAs -Wait

if (Test-Path $Out) { Get-Content $Out -Raw } else { Write-Output '[elevx] no output file was produced' }
