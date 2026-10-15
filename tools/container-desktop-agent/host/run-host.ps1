# Starts the elevated host runner (one UAC) and waits until the container is up.
$dir = "$env:TEMP\opencode\wsmdiag"
Remove-Item "$dir\dd\o.txt", "$dir\dd\q.txt", "$dir\dd\cursor.txt" -ErrorAction SilentlyContinue

Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$dir\dd\host.ps1" | Out-Null

for ($i = 0; $i -lt 150; $i++) {
    Start-Sleep -Seconds 1
    if (Test-Path "$dir\dd\o.txt") {
        $text = Get-Content "$dir\dd\o.txt" -Raw -ErrorAction SilentlyContinue
        if ($text -match '### ready') { break }
    }
}

Get-Content "$dir\dd\o.txt" -ErrorAction SilentlyContinue
Write-Host '==== state ===='
Get-Content "$dir\dd\state.json" -ErrorAction SilentlyContinue
