$ErrorActionPreference = 'Continue'
$files = 'C:\wsm\deskinput.ps1', 'C:\wsm\deskcap.ps1', 'C:\wsm\session-run.ps1'
foreach ($f in $files) {
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$null, [ref]$errors)
    if ($errors -and $errors.Count -gt 0) {
        Write-Output ("PARSE ERRORS in {0}:" -f $f)
        $errors | ForEach-Object { Write-Output ('  ' + $_.Extent.StartLineNumber + ': ' + $_.Message) }
    }
    else { Write-Output ("OK: {0} parses (PS {1})" -f $f, $PSVersionTable.PSVersion) }
}
