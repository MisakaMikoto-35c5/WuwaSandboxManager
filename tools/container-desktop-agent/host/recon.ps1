# Container-side reconnaissance (runs inside the container, elevated in session 0).
$ErrorActionPreference = 'Continue'
function Section($t) { Write-Output ""; Write-Output ("==== $t ====") }

Section 'identity'
(Get-Process -Id $PID) | ForEach-Object { "powershell pid=$($_.Id) session=$($_.SessionId)" }
whoami

Section 'sessions (qwinsta)'
(qwinsta 2>&1 | Out-String)

Section 'processes by session'
(Get-Process | Group-Object SessionId | ForEach-Object {
        "session {0}: {1} processes: {2}" -f $_.Name, $_.Count, (($_.Group | Select-Object -First 8 -ExpandProperty ProcessName) -join ',')
    }) -join "`n"

Section 'tasklist /v (first 60 lines)'
((tasklist /v 2>&1) | Select-Object -First 60) -join "`n"

Section 'privileges'
(whoami /priv 2>&1 | Out-String)

Section 'local accounts'
(net user 2>&1 | Out-String)

Section 'named pipes'
((Get-ChildItem \\.\pipe\ -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join ' | ')

Section 'powershell / compiler'
"PSVersion: " + $PSVersionTable.PSVersion.ToString()
try {
    Add-Type -TypeDefinition 'public class T1 { public static int X() { return 42; } }' -Language CSharp
    "Add-Type OK: " + [T1]::X()
}
catch { "Add-Type FAIL: " + $_.Exception.Message }

Section 'TermService / UmRdpService'
(sc.exe queryex TermService 2>&1 | Out-String)
(sc.exe qc TermService 2>&1 | Out-String)
(sc.exe queryex UmRdpService 2>&1 | Out-String)
(sc.exe qc UmRdpService 2>&1 | Out-String)

Section 'System log: service failures'
(Get-WinEvent -LogName System -MaxEvents 80 -ErrorAction SilentlyContinue |
    Where-Object { $_.Id -in 7000, 7001, 7009, 7011, 7023, 7024, 7031, 7034 } |
    ForEach-Object { "#{0} {1} {2}" -f $_.Id, $_.TimeCreated, ($_.Message -replace "`r?`n", ' ') } |
    Select-Object -First 12) -join "`n"

Section 'Terminal Server registry'
(reg query "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" 2>&1 | Out-String)

Section 'WinStations'
(reg query "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations" 2>&1 | Out-String)

Section 'Hvsi / container markers'
(reg query "HKLM\SYSTEM\CurrentControlSet\Control\Hvsi" 2>&1 | Out-String)
(reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v EditionID 2>&1 | Out-String)
(reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v AllowListenersInsideArgonContainer 2>&1 | Out-String)

Section 'writable C:\wsm'
try {
    New-Item -ItemType Directory -Force C:\wsm | Out-Null
    Set-Content C:\wsm\probe.txt 'ok'
    "write OK: " + (Get-Content C:\wsm\probe.txt -Raw)
}
catch { "write FAIL: " + $_.Exception.Message }

Section 'done'
