$ErrorActionPreference = 'Continue'
$f = 'C:\wsm\shmprobe.ps1'
Write-Output ("file={0} exists={1} size={2}" -f $f, (Test-Path $f), (Get-Item $f -ErrorAction SilentlyContinue).Length)
Write-Output '--- first 3 lines ---'
(Get-Content $f -TotalCount 3) | ForEach-Object { Write-Output ('  | ' + $_) }
Write-Output '--- parse ---'
$errors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$null, [ref]$errors)
if ($errors -and $errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Output (('  LINE {0}: {1}' -f $_.Extent.StartLineNumber, $_.Message)) }
}
else { Write-Output '  no parse errors' }
