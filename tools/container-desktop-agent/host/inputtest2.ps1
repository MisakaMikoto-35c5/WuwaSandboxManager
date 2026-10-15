$ErrorActionPreference = 'Continue'
$sr = 'C:\wsm\session-run.ps1'
$ps = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File'

function Run([string] $cmd, [switch] $vis) {
    if ($vis) {
        $o = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -Visible -CommandLine $cmd 2>&1
    }
    else {
        $o = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $sr -Session 4 -Desktop 'winsta0\Winlogon' -CommandLine $cmd 2>&1
    }
    return ($o | Out-String).Trim()
}

Get-Process cmd, regedt32 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item C:\wsm\verify.txt, C:\wsm\in-before.png, C:\wsm\in-after.png, C:\wsm\deskinput.log -ErrorAction SilentlyContinue

Write-Output '=== 1. start a console that stays open (visible) ==='
Write-Output (Run 'cmd.exe /k title WSM-CONSOLE' -vis)
Start-Sleep -Seconds 6

Write-Output '=== 2. deskinput info ==='
Write-Output (Run "$ps C:\wsm\deskinput.ps1 -Action info")

Write-Output '=== 3. capture BEFORE ==='
Write-Output (Run "$ps C:\wsm\deskcap.ps1 -Once C:\wsm\in-before.png")

Write-Output '=== 4. click into the console, then type a command that writes a file ==='
Write-Output (Run "$ps C:\wsm\deskinput.ps1 -Action moveclick -X 250 -Y 180")
Write-Output (Run "$ps C:\wsm\deskinput.ps1 -Action info")
Write-Output (Run ('{0} C:\wsm\deskinput.ps1 -Action text -Text "echo WSM-INPUT-OK> C:\wsm\verify.txt"' -f $ps))
Write-Output (Run "$ps C:\wsm\deskinput.ps1 -Action keys -Keys ENTER")
Start-Sleep -Seconds 3

Write-Output '=== 5. capture AFTER ==='
Write-Output (Run "$ps C:\wsm\deskcap.ps1 -Once C:\wsm\in-after.png")

Write-Output ('  before=' + (Get-Item C:\wsm\in-before.png -ErrorAction SilentlyContinue).Length + ' bytes  after=' + (Get-Item C:\wsm\in-after.png -ErrorAction SilentlyContinue).Length + ' bytes')

Write-Output '=== 6. NON-VISUAL PROOF: did the typed command run? ==='
Write-Output ('  verify.txt exists=' + (Test-Path C:\wsm\verify.txt) + ' content=' + ((Get-Content C:\wsm\verify.txt -ErrorAction SilentlyContinue) -join ','))

Write-Output '=== 7. deskinput log ==='
(Get-Content C:\wsm\deskinput.log -ErrorAction SilentlyContinue) -join "`n"
