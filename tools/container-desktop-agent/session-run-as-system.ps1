<#
.SYNOPSIS
  Runs session-run.ps1 as SYSTEM (through a one-shot scheduled task) so the
  borrowed console-session token actually creates the process IN that session.

.DESCRIPTION
  Why the extra hop is needed:

    * A container administrator's token has SeDebugPrivilege and
      SeImpersonatePrivilege, but NOT SeAssignPrimaryTokenPrivilege or
      SeTcbPrivilege. CreateProcessAsUser therefore fails outright, and the only
      call that works is CreateProcessWithTokenW.
    * CreateProcessWithTokenW ignores the token's session and creates the child in
      the CALLER's session - so the process lands in the services session, not the
      console session, and draws on the services session's desktop.

  Running the same launcher as SYSTEM fixes both: SYSTEM holds the missing
  privileges, so CreateProcessAsUser with winlogon's primary token creates the
  child in winlogon's session - the console session - on the desktop you ask for.

  The container image's Schedule service is running, so `schtasks /ru SYSTEM` is
  enough; nothing has to be installed as a service. The task action is kept
  quote-free by writing the real arguments to a key=value file.

.PARAMETER Desktop
  Desktop on the console session to create the process on (winsta0\default or
  winsta0\Winlogon).

.PARAMETER CommandLine
  The command line to run there.

.PARAMETER Visible
  Give the child a console window on that desktop (proof that it draws).

.EXAMPLE
  powershell -NoProfile -File C:\wsm\session-run-as-system.ps1 -Desktop winsta0\default -CommandLine 'C:\Windows\System32\cmd.exe' -Visible
#>
[CmdletBinding()]
param(
    [string] $Desktop = 'winsta0\default',

    [Parameter(Mandatory = $true)][string] $CommandLine,

    [switch] $Visible,

    [string] $Directory = 'C:\wsm',

    [string] $TaskName = 'wsm-session-run'
)

$ErrorActionPreference = 'Stop'

New-Item -ItemType Directory -Force $Directory | Out-Null
$argumentFile = Join-Path $Directory 'sys-run.args'
$resultFile = Join-Path $Directory 'sys-run.result'
$loaderFile = Join-Path $Directory 'sys-run.ps1'

Set-Content -Path $argumentFile -Encoding ASCII -Value @(
    "Desktop=$Desktop"
    "CommandLine=$CommandLine"
    ('Visible=' + [int][bool]$Visible)
)

# The task action must stay quote-free, so it runs this tiny loader. The loader
# writes the launcher's whole output to the result file at the end, so the caller
# can wait for a non-empty file.
Set-Content -Path $loaderFile -Encoding ASCII -Value @"
`$text = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -ArgumentFile $argumentFile 2>&1 | Out-String
Set-Content -Path $resultFile -Value `$text
"@

Remove-Item $resultFile -ErrorAction SilentlyContinue
& schtasks.exe /delete /tn $TaskName /f 2>&1 | Out-Null
& schtasks.exe /create /tn $TaskName /tr "powershell.exe -NoProfile -ExecutionPolicy Bypass -File $loaderFile" `
    /sc once /st 23:59 /ru SYSTEM /rl HIGHEST /f | Out-Null
& schtasks.exe /run /tn $TaskName | Out-Null

for ($i = 0; $i -lt 90; $i++) {
    if ((Test-Path $resultFile) -and (Get-Item $resultFile).Length -gt 0) { break }
    Start-Sleep -Milliseconds 500
}
Start-Sleep -Milliseconds 300

$result = Get-Content $resultFile -Raw -ErrorAction SilentlyContinue
if (-not $result) {
    $state = & schtasks.exe /query /tn $TaskName /fo LIST 2>&1 |
        Select-String -Pattern 'Status|Last Result' | ForEach-Object { $_.Line.Trim() }
    $result = 'FAIL no result from the SYSTEM task; task state: ' + ($state -join '; ')
}
& schtasks.exe /delete /tn $TaskName /f 2>&1 | Out-Null
$result
