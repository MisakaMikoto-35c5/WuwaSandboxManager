# Poll the container's deskcap agent and save the first screenshot it sends.
param(
    [int] $Seconds = 180,
    [int] $Port = 5901,
    [string] $Out
)

$dir = "$env:TEMP\opencode\wsmdiag\dd"
if (-not $Out) { $Out = "$dir\container-desktop.png" }
$ip = (Get-Content "$dir\state.json" | ConvertFrom-Json).ip
Write-Host "polling $ip`:$Port for up to $Seconds s ..."

$deadline = (Get-Date).AddSeconds($Seconds)
$saved = $false
while ((Get-Date) -lt $deadline -and -not $saved) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $client.Connect($ip, $Port)
        $stream = $client.GetStream()
        $lengthBytes = New-Object byte[] 4
        $read = 0
        while ($read -lt 4) { $n = $stream.Read($lengthBytes, $read, 4 - $read); if ($n -le 0) { throw 'eof' }; $read += $n }
        $length = [BitConverter]::ToInt32($lengthBytes, 0)
        $buffer = New-Object byte[] $length
        $read = 0
        while ($read -lt $length) { $n = $stream.Read($buffer, $read, $length - $read); if ($n -le 0) { throw 'eof' }; $read += $n }
        [IO.File]::WriteAllBytes($Out, $buffer)
        $client.Close()
        Write-Host ("GOT screenshot: {0:N0} bytes -> {1}" -f $length, $Out)
        $saved = $true
    }
    catch {
        Start-Sleep -Seconds 5
    }
}

if (-not $saved) { Write-Host "no screenshot within $Seconds s" }
