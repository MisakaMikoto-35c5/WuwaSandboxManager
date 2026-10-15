# One elevated, long-running host runner (avoids repeated UAC + wedged -Wait).
#   - starts a held process-isolated container
#   - services a command queue so the non-elevated side can drive it
#
#   queue lines (dd\q.txt, append-only):
#     @copy|<host path>|<container relative path>   copy a file into C:\ of the container
#     @wait|<seconds>
#     @status
#     @stop
#     <anything else>                              a command line to run in the container
#
#   everything is appended to dd\o.txt, plus dd\probe.out (the container probe's own output)
$ErrorActionPreference = 'Continue'

$dir = "$env:TEMP\opencode\wsmdiag"
$exe = "$dir\HcsProbe\bin\Release\net10.0-windows\hcsprobe.exe"
$layer = 'C:\wsm-images\servercore-ltsc2022'
$id = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeee0e02'
$scratch = 'C:\wsmprobe\dd3'
$qfile = "$dir\dd\q.txt"
$ofile = "$dir\dd\o.txt"
$mountFile = "$dir\dd\mount.txt"

function Log($t) { Add-Content -Path $ofile -Value $t }

# Only one runner may own the queue; kill any previous one (we are elevated).
Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*dd\host.ps1*' -and $_.ProcessId -ne $PID } |
    ForEach-Object {
        Log ('### killing previous host runner pid ' + $_.ProcessId)
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
Start-Sleep -Seconds 1

Remove-Item $ofile, $qfile -ErrorAction SilentlyContinue
"host runner started $(Get-Date -Format o)" | Set-Content $ofile

Log '### cleanup leftover probes'
Get-Process hcsprobe -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3
Log ('unprepare: ' + ((& $exe lunprepare $layer 2>&1) -join ' '))
Log ('deactivate: ' + ((& $exe ldeactivate $layer 2>&1) -join ' '))

Log '### NAT'
$natId = $null
try {
    $n = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
    $natId = ((($n -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output |
        Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID
}
catch { }
if (-not $natId) {
    & $exe hnsnat 2>&1 | Out-Null
    Start-Sleep -Seconds 2
    try {
        $n = (& $exe hns GET "/networks/" "" 2>&1) -join "`n"
        $natId = ((($n -split "`n" | Select-Object -Skip 1) -join "`n") | ConvertFrom-Json).Output |
            Where-Object { $_.Type -eq 'NAT' } | Select-Object -First 1 -ExpandProperty ID
    }
    catch { }
}
Log "natId=$natId"

Remove-Item -Recurse -Force $scratch -ErrorAction SilentlyContinue
$env:WSMPROBE_HOLD = '7200'
Log '### starting container (hold 7200s)'
$probe = Start-Process -FilePath $exe -ArgumentList @('argonnet3', $id, $scratch, $layer, $natId) `
    -RedirectStandardOutput "$dir\dd\probe.out" -RedirectStandardError "$dir\dd\probe.err" `
    -NoNewWindow -PassThru
Log ("probe pid: " + $probe.Id)

# Wait for the container's ipconfig to show up, then publish the ip + layer mount.
$ip = $null
$mount = $null
for ($i = 0; $i -lt 90 -and -not $ip; $i++) {
    Start-Sleep -Seconds 1
    $text = Get-Content "$dir\dd\probe.out" -Raw -ErrorAction SilentlyContinue
    if ($text) {
        $m = [regex]::Match($text, 'IPv4[^0-9]*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)')
        if ($m.Success) { $ip = $m.Groups[1].Value }
        $m2 = [regex]::Match($text, 'Storage"\s*:\s*\{[^}]*"Path"\s*:\s*"([^"]+)"')
        if ($m2.Success) {
            # the probe prints JSON, so backslashes arrive doubled
            $mount = $m2.Groups[1].Value.Replace('\\', '\')
        }
    }
}
if (-not $mount) {
    # fall back: ask the probe for the layer mount path
    $mount = ((& $exe lmount $scratch 2>&1) -join ' ').Trim()
    if ($mount -notmatch '^\\\\\?\\Volume') {
        # the probe prints the volume path with a trailing backslash
        $mount = [regex]::Match($mount, '\\\\\?\\Volume\{[^}]+\}\\?').Value
    }
}
Log "ip=$ip mount=$mount"
@{ id = $id; ip = $ip; mount = $mount; scratch = $scratch } | ConvertTo-Json | Set-Content "$dir\dd\state.json" -Encoding UTF8
if ($mount) { Set-Content $mountFile -Value $mount }

Log '### ready'
$position = 0
while ($true) {
    Start-Sleep -Milliseconds 600
    if (-not (Test-Path $qfile)) { continue }

    $lines = @(Get-Content $qfile -ErrorAction SilentlyContinue)
    while ($position -lt $lines.Count) {
        $line = [string] $lines[$position]
        $position++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        if ($line -eq '@stop') {
            Log '@stop - killing the container probe and exiting'
            Stop-Process -Id $probe.Id -Force -ErrorAction SilentlyContinue
            exit
        }
        if ($line -eq '@status') {
            Log ('@status alive=' + (Get-Date -Format o) + ' probeRunnning=' + [bool](Get-Process -Id $probe.Id -ErrorAction SilentlyContinue))
            continue
        }
        if ($line -like '@wait|*') {
            $seconds = [int]($line.Split('|')[1])
            Start-Sleep -Seconds $seconds
            continue
        }
        if ($line -like '@copy|*') {
            $parts = $line.Split('|')
            $mount = (Get-Content $mountFile -Raw -ErrorAction SilentlyContinue).Trim().TrimEnd('\')
            $destination = $mount + '\' + $parts[2].TrimStart('\')
            try {
                # PowerShell's FileSystem provider (and Path.GetDirectoryName)
                # reject the \\?\Volume{...} form; the raw .NET File API accepts
                # it, so build the folder name with a plain string operation.
                $folder = $destination.Substring(0, $destination.LastIndexOf('\'))
                [void][IO.Directory]::CreateDirectory($folder)
                [IO.File]::Copy($parts[1], $destination, $true)
                Log ('@copy OK ' + $parts[1] + ' -> ' + $destination + ' (' + (New-Object IO.FileInfo($destination)).Length + ' bytes)')
            }
            catch { Log ('@copy FAIL ' + $_.Exception.Message) }
            Log '### END'
            continue
        }

        Log ('### CMD ' + ($line.Substring(0, [Math]::Min(140, $line.Length))) + ' ...')
        Remove-Item "$dir\dd\cmd.out", "$dir\dd\cmd.err" -ErrorAction SilentlyContinue
        $child = Start-Process -FilePath $exe -ArgumentList @('sh', $id, $line) `
            -RedirectStandardOutput "$dir\dd\cmd.out" -RedirectStandardError "$dir\dd\cmd.err" `
            -NoNewWindow -PassThru
        if (-not $child.WaitForExit(600000)) {
            $child.Kill()
            Log '### TIMEOUT (probe killed)'
        }
        Log ((Get-Content "$dir\dd\cmd.out" -Raw -ErrorAction SilentlyContinue))
        $stderr = Get-Content "$dir\dd\cmd.err" -Raw -ErrorAction SilentlyContinue
        if ($stderr) { Log ('### STDERR ' + $stderr) }
        Log '### END'
    }
}
