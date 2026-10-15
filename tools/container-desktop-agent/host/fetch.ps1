# Connects to the container's deskcap agent and saves the PNG it sends.
param(
    [string] $Ip,
    [int] $Port = 5901,
    [Parameter(Mandatory = $true)][string] $Out
)

if (-not $Ip) {
    $Ip = (Get-Content "$env:TEMP\opencode\wsmdiag\dd-state.json" | ConvertFrom-Json).ip
}

$client = New-Object System.Net.Sockets.TcpClient
$client.Connect($Ip, $Port)
try {
    $stream = $client.GetStream()
    $lengthBytes = New-Object byte[] 4
    $read = 0
    while ($read -lt 4) {
        $n = $stream.Read($lengthBytes, $read, 4 - $read)
        if ($n -le 0) { throw 'connection closed before the length' }
        $read += $n
    }
    $length = [BitConverter]::ToInt32($lengthBytes, 0)
    $buffer = New-Object byte[] $length
    $read = 0
    while ($read -lt $length) {
        $n = $stream.Read($buffer, $read, $length - $read)
        if ($n -le 0) { throw 'connection closed mid-payload' }
        $read += $n
    }
    [IO.File]::WriteAllBytes($Out, $buffer)
}
finally {
    $client.Close()
}

Write-Host ("saved {0:N0} bytes to {1}" -f (Get-Item $Out).Length, $Out)
