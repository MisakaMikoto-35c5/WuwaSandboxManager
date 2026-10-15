# Pushes the agent scripts (from the repo) into the container's C:\wsm.
# The payload is gzipped before base64 so the -EncodedCommand stays well under
# the ~32 KB CreateProcess command-line limit.
$dir = "$env:TEMP\opencode\wsmdiag"
$repo = 'C:\Users\User\Documents\WindowsSandboxManager\tools\container-desktop-agent'
Add-Type -AssemblyName System.IO.Compression

function Compressed64([string] $path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    $memory = New-Object System.IO.MemoryStream
    $gzip = New-Object System.IO.Compression.GZipStream($memory, [System.IO.Compression.CompressionMode]::Compress, $true)
    $gzip.Write($bytes, 0, $bytes.Length)
    $gzip.Dispose()
    return [Convert]::ToBase64String($memory.ToArray())
}

$sessionRun = Compressed64 "$repo\session-run.ps1"
$deskcap = Compressed64 "$repo\deskcap.ps1"
Write-Host ("session-run payload: {0:N0} chars; deskcap payload: {1:N0} chars" -f $sessionRun.Length, $deskcap.Length)

$action = @"
`$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.IO.Compression
function Expand64([string] `$b64, [string] `$out) {
    `$input = New-Object System.IO.MemoryStream ([Convert]::FromBase64String(`$b64))
    `$gzip = New-Object System.IO.Compression.GZipStream(`$input, [System.IO.Compression.CompressionMode]::Decompress)
    `$file = [IO.File]::Create(`$out)
    `$gzip.CopyTo(`$file)
    `$file.Close(); `$gzip.Dispose()
}
New-Item -ItemType Directory -Force C:\wsm | Out-Null
Expand64 '$sessionRun' 'C:\wsm\session-run.ps1'
Expand64 '$deskcap' 'C:\wsm\deskcap.ps1'
Write-Output ('session-run.ps1 bytes: ' + (Get-Item C:\wsm\session-run.ps1).Length)
Write-Output ('deskcap.ps1 bytes: ' + (Get-Item C:\wsm\deskcap.ps1).Length)
Write-Output ('session-run parses: ' + [bool]([ScriptBlock]::Create((Get-Content C:\wsm\session-run.ps1 -Raw))))
Write-Output ('deskcap parses: ' + [bool]([ScriptBlock]::Create((Get-Content C:\wsm\deskcap.ps1 -Raw))))
"@

$actionPath = "$dir\dd\deploy-action.ps1"
Set-Content $actionPath -Value $action
Write-Host ("action script: {0:N0} chars -> encoded command line about {1:N0} chars" -f $action.Length, ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($action))).Length)
& "$dir\dd\in.ps1" -Script $actionPath
