# container-mirror4.ps1 - NO ELEVATION NEEDED.
#   * stage the fixed read side into the live container's root (the host can write there)
#   * start a window + the read side inside the container via the already-elevated runner
#   * mirror the container's windows on the host, drag one, and read the container window's
#     published rectangle back before/after.
$ErrorActionPreference = 'Continue'
$dd = "$env:TEMP\opencode\wsmdiag\dd"
$repo = 'C:\Users\User\Documents\WindowsSandboxManager'
$wsm = "$repo\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe"
$state = Get-Content (Join-Path $dd 'state.json') -Raw | ConvertFrom-Json
$root = $state.mount
$wsmDir = [System.IO.Path]::Combine($root, 'wsm')
$mirror = [System.IO.Path]::Combine($wsmDir, 'mirror.shm')
function Note([string] $m) { Write-Output $m }
function Run-In([string] $line) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dd 'q.ps1') -Line $line -TimeoutSec 180 2>&1 | Out-Null
}

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class Cm4 {
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

Note ("container root = " + $root)

Write-Output ''
Write-Output '===== 1. stage the fixed read side into the container ====='
$wrote = $false
try {
    [System.IO.File]::Copy((Join-Path $repo 'src\10-Hcs\Wsm.Hcs.Storage\Images\wsm-desktop-read.ps1'),
        [System.IO.Path]::Combine($wsmDir, 'wsm-desktop-read.ps1'), $true)
    [System.IO.File]::Copy((Join-Path $repo 'src\10-Hcs\Wsm.Hcs.Storage\Images\wsm-desktop-read.cmd'),
        [System.IO.Path]::Combine($wsmDir, 'wsm-desktop-read.cmd'), $true)
    $wrote = ([System.IO.FileInfo]::new([System.IO.Path]::Combine($wsmDir, 'wsm-desktop-read.ps1'))).Length -gt 20000
} catch { Note ("  copy failed: " + $_.Exception.Message) }
Note ("  staged=" + $wrote)
foreach ($f in @('agent-stop.txt', 'mirror.shm', 'read.log', 'read.out', 'mirror-view.log')) {
    $p = [System.IO.Path]::Combine($wsmDir, $f)
    if ([System.IO.File]::Exists($p)) { [System.IO.File]::Delete($p) }
}

Write-Output ''
Write-Output '===== 2. a window + the read side, both started inside the container ====='
Run-In 'cmd.exe /c echo PING'
Run-In 'cmd.exe /c start /b notepad'
Start-Sleep -Seconds 5
Run-In 'cmd.exe /c C:\wsm\wsm-winlogon-wrap.cmd'
for ($i = 0; $i -lt 40 -and -not [System.IO.File]::Exists($mirror); $i++) { Start-Sleep -Seconds 2 }
Note ("  mirror.shm published = " + [System.IO.File]::Exists($mirror))

Write-Output ''
Write-Output '===== 3. the container window as the read side sees it (before) ====='
$before = & $wsm desktop-view --frame $mirror --print-table 2>&1 | Select-String -Pattern 'Geometry|Windows|id=0x'
$before | ForEach-Object { Note ("  " + $_.Line) }
$beforeRect = $null
foreach ($l in $before) { if ($l.Line -match 'id=0x[0-9A-F]+ rect=\((-?\d+),(-?\d+)') { $beforeRect = @{ X = [int]$Matches[1]; Y = [int]$Matches[2] } } }

Write-Output ''
Write-Output '===== 4. mirror the container windows on the host ====='
$view = Start-Process $wsm -PassThru -ArgumentList @('desktop-view','--mode','windows','--frame',$mirror,
    '--seconds','90','--offset-x','420','--offset-y','240')
Start-Sleep -Seconds 7
$mirrorInfo = [Cm4]::FindMirror('Notepad')
Note ("  mirror window: " + $(if ($mirrorInfo) { $mirrorInfo } else { '(none)' }))

Write-Output ''
Write-Output '===== 5. drag the mirrored window by (+90,+60) ====='
if ($mirrorInfo -match 'hwnd=0x([0-9A-F]+)') {
    $hwnd = [IntPtr][Convert]::ToInt64($Matches[1], 16)
    [void][Cm4]::PostMessage($hwnd, 0x0201, [Cm4]::LeftButton, [Cm4]::Pt(60, 12))
    Start-Sleep -Milliseconds 250
    foreach ($step in 1..6) {
        [void][Cm4]::PostMessage($hwnd, 0x0200, [Cm4]::LeftButton, [Cm4]::Pt((60 + $step * 15), (12 + $step * 10)))
        Start-Sleep -Milliseconds 170
    }
    [void][Cm4]::PostMessage($hwnd, 0x0202, [IntPtr]::Zero, [Cm4]::Pt(150, 72))
    Start-Sleep -Seconds 6
    Note '  drag posted'
} else { Note '  nothing to drag' }

Write-Output ''
Write-Output '===== 6. the container window as the read side sees it (after) ====='
$after = & $wsm desktop-view --frame $mirror --print-table 2>&1 | Select-String -Pattern 'Windows|id=0x'
$after | ForEach-Object { Note ("  " + $_.Line) }
$afterRect = $null
foreach ($l in $after) { if ($l.Line -match 'id=0x[0-9A-F]+ rect=\((-?\d+),(-?\d+)') { $afterRect = @{ X = [int]$Matches[1]; Y = [int]$Matches[2] } } }

Write-Output ''
if ($beforeRect -and $afterRect) {
    $dx = $afterRect.X - $beforeRect.X
    $dy = $afterRect.Y - $beforeRect.Y
    Note ("[=] container window moved by (" + $dx + "," + $dy + ")  " +
        $(if ([Math]::Abs($dx) -ge 60 -and [Math]::Abs($dy) -ge 40) { 'DRAG OK' } else { 'DRAG FAIL' }))
} else {
    Note '[=] could not read the window rectangle before/after'
}

Write-Output ''
Write-Output '===== 7. evidence ====='
foreach ($f in @('read.log', 'read.out', 'mirror-view.log')) {
    $p = [System.IO.Path]::Combine($wsmDir, $f)
    if ([System.IO.File]::Exists($p)) {
        Note ("--- " + $f + " (tail) ---")
        Get-Content $p -Tail 10 | ForEach-Object { Note ("  " + $_) }
    } else { Note ("--- " + $f + " (missing)") }
}

Write-Output ''
Write-Output '===== 8. cleanup ====='
Get-Process wsm -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
[System.IO.File]::WriteAllText([System.IO.Path]::Combine($wsmDir, 'agent-stop.txt'), 'stop')
Run-In 'cmd.exe /c taskkill /f /im notepad.exe'
