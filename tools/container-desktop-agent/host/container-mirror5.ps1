# container-mirror5.ps1 - runs ELEVATED (one right-click), self-contained, no UAC inside:
#   1. starts the HCS harness runner directly (host.ps1; no nested RunAs)
#   2. stages the fixed read side into the container root
#   3. starts a window INSIDE the container + the read side (through the runner)
#   4. mirrors the container's windows on the host as real windows
#   5. drags one mirror window and reads the container window's rectangle back
$ErrorActionPreference = 'Continue'
$dd = "$env:TEMP\opencode\wsmdiag\dd"
$repo = 'C:\Users\User\Documents\WindowsSandboxManager'
$wsm = "$repo\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe"
$o = Join-Path $dd 'o.txt'
function Note([string] $m) { Write-Output $m; $script:log += $m + "`r`n" }
$script:log = ''
function Run-In([string] $line) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dd 'q.ps1') -Line $line -TimeoutSec 180 2>&1 | Out-Null
}

Write-Output '===== 1. start the HCS harness runner (fresh container) ====='
Remove-Item (Join-Path $dd 'state.json') -Force -ErrorAction SilentlyContinue
Remove-Item $o -Force -ErrorAction SilentlyContinue
$runner = Start-Process powershell.exe -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $dd 'host.ps1'))
$state = $null
for ($i = 0; $i -lt 90; $i++) {
    Start-Sleep -Seconds 3
    if (Test-Path (Join-Path $dd 'state.json')) {
        $state = Get-Content (Join-Path $dd 'state.json') -Raw | ConvertFrom-Json
        if ($state.mount) { break }
    }
}
if (-not $state -or -not $state.mount) {
    Note '[!] the runner did not produce a container; see o.txt'
    Get-Content $o -Tail 20 | ForEach-Object { Note ("  " + $_) }
    exit 1
}
$root = $state.mount
$wsmDir = [System.IO.Path]::Combine($root, 'wsm')
Note ("  container=" + $state.id + " ip=" + $state.ip)
Note ("  root=" + $root)
Start-Sleep -Seconds 5

Write-Output ''
Write-Output '===== 2. health check ====='
Run-In 'cmd.exe /c echo ALIVE'
Start-Sleep -Seconds 2
Get-Content $o -Tail 12 | Select-String -Pattern 'ALIVE|exit=' | Select-Object -First 2 | ForEach-Object { Note ("  " + $_.Line.Trim()) }

Write-Output ''
Write-Output '===== 3. stage the fixed read side into the container ====='
try {
    [System.IO.File]::Copy((Join-Path $repo 'src\10-Hcs\Wsm.Hcs.Storage\Images\wsm-desktop-read.ps1'),
        [System.IO.Path]::Combine($wsmDir, 'wsm-desktop-read.ps1'), $true)
    [System.IO.File]::Copy((Join-Path $repo 'src\10-Hcs\Wsm.Hcs.Storage\Images\wsm-desktop-read.cmd'),
        [System.IO.Path]::Combine($wsmDir, 'wsm-desktop-read.cmd'), $true)
    Note ("  staged size=" + ([System.IO.FileInfo]::new([System.IO.Path]::Combine($wsmDir, 'wsm-desktop-read.ps1'))).Length)
} catch { Note ("  stage failed: " + $_.Exception.Message) }

Write-Output ''
Write-Output '===== 4. window + read side, inside the container ====='
Run-In 'cmd.exe /c start /b notepad'
Start-Sleep -Seconds 6
Run-In 'cmd.exe /c C:\wsm\wsm-winlogon-wrap.cmd'
$mirror = [System.IO.Path]::Combine($wsmDir, 'mirror.shm')
for ($i = 0; $i -lt 45 -and -not [System.IO.File]::Exists($mirror); $i++) { Start-Sleep -Seconds 2 }
Note ("  mirror.shm published = " + [System.IO.File]::Exists($mirror))
if (-not [System.IO.File]::Exists($mirror)) {
    $readOut = [System.IO.Path]::Combine($wsmDir, 'read.out')
    if ([System.IO.File]::Exists($readOut)) {
        Note '  --- read.out (tail) ---'
        Get-Content $readOut -Tail 12 | ForEach-Object { Note ("    " + $_) }
    }
}

Write-Output ''
Write-Output '===== 5. the container window as the read side sees it (before) ====='
& $wsm desktop-view --frame $mirror --print-table 2>&1 | Select-String -Pattern 'Geometry|Windows|id=0x' | ForEach-Object { Note ("  " + $_.Line) }

Write-Output ''
Write-Output '===== 6. mirror the container windows on the host + drag one ====='
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class Cm5 {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    delegate bool Proc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(Proc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder t, int m);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    public static IntPtr LeftButton = (IntPtr)0x0001;
    public static IntPtr Pt(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }
    public static string FindMirror(string titlePart) {
        IntPtr found = IntPtr.Zero; string detail = "";
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(300); GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0) return true;
            int pid; GetWindowThreadProcessId(h, out pid);
            string name = ""; try { name = Process.GetProcessById(pid).ProcessName; } catch {}
            if (!string.Equals(name, "wsm", StringComparison.OrdinalIgnoreCase)) return true;
            found = h; detail = string.Format("hwnd=0x{0:X} pid={1} \"{2}\"", h.ToInt64(), pid, sb.ToString());
            return false;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? "" : detail;
    }
}
'@

$view = Start-Process $wsm -PassThru -ArgumentList @('desktop-view','--mode','windows','--frame',$mirror,
    '--seconds','120','--offset-x','420','--offset-y','240')
Start-Sleep -Seconds 8
$mirrorInfo = [Cm5]::FindMirror('Notepad')
Note ("  mirror window: " + $(if ($mirrorInfo) { $mirrorInfo } else { '(none)' }))
if ($mirrorInfo -match 'hwnd=0x([0-9A-F]+)') {
    $hwnd = [IntPtr][Convert]::ToInt64($Matches[1], 16)
    [void][Cm5]::PostMessage($hwnd, 0x0201, [Cm5]::LeftButton, [Cm5]::Pt(60, 12))
    Start-Sleep -Milliseconds 250
    foreach ($step in 1..6) {
        [void][Cm5]::PostMessage($hwnd, 0x0200, [Cm5]::LeftButton, [Cm5]::Pt((60 + $step * 15), (12 + $step * 10)))
        Start-Sleep -Milliseconds 170
    }
    [void][Cm5]::PostMessage($hwnd, 0x0202, [IntPtr]::Zero, [Cm5]::Pt(150, 72))
    Start-Sleep -Seconds 6
    Note '  drag posted (+90,+60)'
}

Write-Output ''
Write-Output '===== 7. the container window after the drag ====='
& $wsm desktop-view --frame $mirror --print-table 2>&1 | Select-String -Pattern 'Windows|id=0x' | ForEach-Object { Note ("  " + $_.Line) }

Write-Output ''
Write-Output '===== 8. evidence ====='
foreach ($f in @('read.log', 'read.out', 'mirror-view.log')) {
    $p = [System.IO.Path]::Combine($wsmDir, $f)
    if ([System.IO.File]::Exists($p)) {
        Note ("--- " + $f + " (tail) ---")
        Get-Content $p -Tail 10 | ForEach-Object { Note ("  " + $_) }
    } else { Note ("--- " + $f + " (missing)") }
}

Write-Output ''
Write-Output '===== 9. cleanup ====='
Get-Process wsm -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
[System.IO.File]::WriteAllText([System.IO.Path]::Combine($wsmDir, 'agent-stop.txt'), 'stop')
Run-In 'cmd.exe /c taskkill /f /im notepad.exe'
[System.IO.File]::WriteAllText((Join-Path $dd 'container-mirror5.out'), $script:log)
