# Appends one line to the runner's queue and prints only the new output.
param(
    [Parameter(Mandatory = $true)][string] $Line,
    [int] $TimeoutSec = 300
)

$dir = "$env:TEMP\opencode\wsmdiag"
$ofile = "$dir\dd\o.txt"
$qfile = "$dir\dd\q.txt"
$cursorFile = "$dir\dd\cursor.txt"

$cursor = if (Test-Path $cursorFile) { [int](Get-Content $cursorFile -Raw).Trim() } else { 0 }
$before = (Select-String -Path $ofile -Pattern '^### END$' -ErrorAction SilentlyContinue | Measure-Object).Count

Add-Content -Path $qfile -Value $Line

$deadline = (Get-Date).AddSeconds($TimeoutSec)
do {
    Start-Sleep -Milliseconds 700
    $now = (Select-String -Path $ofile -Pattern '^### END$' -ErrorAction SilentlyContinue | Measure-Object).Count
    if ($now -gt $before) { break }
} while ((Get-Date) -lt $deadline)

$lines = @(Get-Content $ofile)
if ($lines.Count -gt $cursor) {
    ($lines[$cursor..($lines.Count - 1)]) -join "`n"
}
Set-Content $cursorFile -Value $lines.Count
