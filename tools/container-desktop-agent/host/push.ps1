# Pushes a local file into the container (gzip + base64 through the queue, so the
# command line stays well under Windows' ~32 KB CreateProcess limit).
param(
    [Parameter(Mandatory = $true)][string] $Local,
    [Parameter(Mandatory = $true)][string] $Remote
)

$dir = "$env:TEMP\opencode\wsmdiag\dd"
Add-Type -AssemblyName System.IO.Compression

$bytes = [IO.File]::ReadAllBytes($Local)
$memory = New-Object System.IO.MemoryStream
$gzip = New-Object System.IO.Compression.GZipStream($memory, [System.IO.Compression.CompressionMode]::Compress, $true)
$gzip.Write($bytes, 0, $bytes.Length)
$gzip.Dispose()
$payload = [Convert]::ToBase64String($memory.ToArray())

$script = @"
`$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression
`$input = [System.IO.MemoryStream]::new([Convert]::FromBase64String('$payload'))
`$gzip = [System.IO.Compression.GZipStream]::new(`$input, [System.IO.Compression.CompressionMode]::Decompress)
`$output = [System.IO.MemoryStream]::new()
`$gzip.CopyTo(`$output)
New-Item -ItemType Directory -Force '$(Split-Path $Remote -Parent)' | Out-Null
[IO.File]::WriteAllBytes('${Remote}', `$output.ToArray())
Write-Output ('pushed file, bytes = ' + (Get-Item '${Remote}').Length)
"@

$actionPath = "$dir\push-action.ps1"
Set-Content $actionPath -Value $script
Write-Host ("payload {0:N0} chars -> action {1:N0} chars" -f $payload.Length, $script.Length)
& "$dir\ps.ps1" -Script $actionPath
