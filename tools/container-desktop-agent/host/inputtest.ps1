$ErrorActionPreference = 'Continue'
$sr = 'C:\wsm\session-run.ps1'
$ps = 'powershell.exe -NoProfile -ExecutionPolicy Bypass'

function In-Session([string] $cmd) {
    $out = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -CommandLine $cmd 2>&1
    return ($out | Out-String).Trim()
}

Write-Output '=== launch regedt32 in session 4 / winsta0\Winlogon ==='
Write-Output (In-Session 'regedt32.exe')
Start-Sleep -Seconds 8

Write-Output '=== capture BEFORE ==='
Write-Output (In-Session ("$ps -File C:\wsm\deskcap.ps1 -Once C:\wsm\before.png"))
Write-Output ('  before.png = ' + (Get-Item C:\wsm\before.png -ErrorAction SilentlyContinue).Length + ' bytes')

Write-Output '=== deskinput info (before input) ==='
Write-Output (In-Session ("$ps -File C:\wsm\deskinput.ps1 -Action info"))

Write-Output '=== send F3 (Registry Editor: File-ish "Find" dialog) ==='
Write-Output (In-Session ("$ps -File C:\wsm\deskinput.ps1 -Action keys -Keys F3"))
Start-Sleep -Seconds 3

Write-Output '=== deskinput info (after input) ==='
Write-Output (In-Session ("$ps -File C:\wsm\deskinput.ps1 -Action info"))

Write-Output '=== capture AFTER ==='
Write-Output (In-Session ("$ps -File C:\wsm\deskcap.ps1 -Once C:\wsm\after.png"))
Write-Output ('  after.png = ' + (Get-Item C:\wsm\after.png -ErrorAction SilentlyContinue).Length + ' bytes')

Write-Output '=== also type something into the Find box ==='
Write-Output (In-Session ("$ps -File C:\wsm\deskinput.ps1 -Action text -Text wsmprobe"))
Start-Sleep -Seconds 2
Write-Output (In-Session ("$ps -File C:\wsm\deskcap.ps1 -Once C:\wsm\after2.png"))
Write-Output ('  after2.png = ' + (Get-Item C:\wsm\after2.png -ErrorAction SilentlyContinue).Length + ' bytes')

Write-Output '=== deskinput log ==='
(Get-Content C:\wsm\deskinput.log -Tail 24 -ErrorAction SilentlyContinue) -join "`n"
