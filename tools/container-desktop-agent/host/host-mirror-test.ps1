# host-mirror-test.ps1 - regression test for the NEW capture path (per-window PrintWindow
# into a DIB section over the shared memory), on the host desktop. No elevation.
$ErrorActionPreference = 'Continue'
$dd = "$env:TEMP\opencode\wsmdiag\dd"
$repo = 'C:\Users\User\Documents\WindowsSandboxManager'
$wsm = "$repo\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe"
$dir = "$env:TEMP\opencode\wsmdiag\hostmirror"
Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$mirror = Join-Path $dir 'mirror.shm'
$filter = 'wsm-mirror-target'
function Note([string] $m) { Write-Output $m }

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
public static class Hm {
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    delegate bool Proc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(Proc p, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder t, int m);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);

    public static IntPtr LeftButton = (IntPtr)0x0001;
    public static IntPtr Pt(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }

    public static string Find(string titlePart, string processName) {
        IntPtr found = IntPtr.Zero; string detail = "";
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(300); GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString().IndexOf(titlePart, StringComparison.OrdinalIgnoreCase) < 0) return true;
            if (processName != null && processName.Length > 0) {
                int pid; GetWindowThreadProcessId(h, out pid);
                string name = "";
                try { name = Process.GetProcessById(pid).ProcessName; } catch (Exception) { }
                if (!string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)) return true;
            }
            found = h;
            detail = string.Format("hwnd=0x{0:X} \"{1}\"", h.ToInt64(), sb.ToString());
            return false;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? "" : detail;
    }

    /// <summary>Ratio of non-black pixels inside a window's rectangle in the shared buffer.</summary>
    public static double Ink(string path, int x, int y, int w, int h, int stride) {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var row = new byte[w * 4];
            int total = 0, nonBlack = 0;
            for (int r = 0; r < h; r += 3) {
                stream.Position = 64 + ((long)(y + r) * stride) + (x * 4);
                int got = stream.Read(row, 0, row.Length);
                for (int i = 0; i + 2 < got; i += 8) {
                    total++;
                    if (row[i] > 8 || row[i + 1] > 8 || row[i + 2] > 8) nonBlack++;
                }
            }
            return total == 0 ? 0 : (double)nonBlack / total;
        }
    }
}
'@

Write-Output '===== 1. a window to mirror ====='
$target = Start-Process cmd.exe -PassThru -ArgumentList @('/k', "title $filter && echo host mirror test")
Start-Sleep -Seconds 3
$info = [Hm]::Find($filter, $null)
$targetHwnd = [IntPtr]::Zero
if ($info -match 'hwnd=0x([0-9A-F]+)') { $targetHwnd = [IntPtr][Convert]::ToInt64($Matches[1], 16) }
[void][Hm]::SetWindowPos($targetHwnd, [IntPtr]::Zero, 80, 80, 480, 300, 0x0004)
Start-Sleep -Seconds 1
Note ("  target " + $info)

Write-Output '===== 2. read side (new per-window DIB capture path) ====='
$read = Start-Process powershell.exe -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass',
    '-File', "$repo\src\10-Hcs\Wsm.Hcs.Storage\Images\wsm-desktop-read.ps1",
    '-AgentDirectory', $dir, '-MirrorFile', 'mirror.shm', '-Seconds', 120,
    '-Width', 1024, '-Height', 768, '-Fps', 4, '-TitleFilter', $filter)
for ($i = 0; $i -lt 30 -and -not (Test-Path $mirror); $i++) { Start-Sleep -Milliseconds 500 }

Write-Output '===== 3. published table (before) + pixel content ====='
$before = & $wsm desktop-view --frame $mirror --print-table 2>&1 | Select-String -Pattern 'Geometry|Windows|id=0x'
$before | ForEach-Object { Note ("  " + $_.Line) }
$beforeRect = $null
foreach ($l in $before) { if ($l.Line -match 'rect=\((-?\d+),(-?\d+) (\d+)x(\d+)\)') { $beforeRect = @{ X=[int]$Matches[1]; Y=[int]$Matches[2]; W=[int]$Matches[3]; H=[int]$Matches[4] } } }
$ink = -1.0
if ($beforeRect) { $ink = [Hm]::Ink($mirror, $beforeRect.X, $beforeRect.Y, $beforeRect.W, $beforeRect.H, 4096) }
Note ("  non-black ratio inside the window rect = " + [Math]::Round($ink * 100, 1) + " %")

Write-Output '===== 4. mirror it + drag by (+90,+60) ====='
$view = Start-Process $wsm -PassThru -ArgumentList @('desktop-view','--mode','windows','--frame',$mirror,
    '--seconds','60','--offset-x','420','--offset-y','240')
Start-Sleep -Seconds 7
$mirrorInfo = [Hm]::Find($filter, 'wsm')
Note ("  mirror window: " + $(if ($mirrorInfo) { $mirrorInfo } else { '(none)' }))
if ($mirrorInfo -match 'hwnd=0x([0-9A-F]+)') {
    $mh = [IntPtr][Convert]::ToInt64($Matches[1], 16)
    [void][Hm]::PostMessage($mh, 0x0201, [Hm]::LeftButton, [Hm]::Pt(60, 12))
    Start-Sleep -Milliseconds 250
    foreach ($step in 1..6) {
        [void][Hm]::PostMessage($mh, 0x0200, [Hm]::LeftButton, [Hm]::Pt((60 + $step * 15), (12 + $step * 10)))
        Start-Sleep -Milliseconds 170
    }
    [void][Hm]::PostMessage($mh, 0x0202, [IntPtr]::Zero, [Hm]::Pt(150, 72))
    Start-Sleep -Seconds 5
}

Write-Output '===== 5. published table (after) ====='
$after = & $wsm desktop-view --frame $mirror --print-table 2>&1 | Select-String -Pattern 'id=0x'
$after | ForEach-Object { Note ("  " + $_.Line) }
$afterRect = $null
foreach ($l in $after) { if ($l.Line -match 'rect=\((-?\d+),(-?\d+)') { $afterRect = @{ X=[int]$Matches[1]; Y=[int]$Matches[2] } } }

Write-Output '===== 6. verdict ====='
if ($beforeRect -and $afterRect) {
    $dx = $afterRect.X - $beforeRect.X; $dy = $afterRect.Y - $beforeRect.Y
    Note ("  window moved by (" + $dx + "," + $dy + ")")
    Note ("[=] capture=" + $(if ($ink -gt 0.02) { 'ok' } else { 'FAIL' }) +
        " display=" + $(if ($mirrorInfo) { 'ok' } else { 'FAIL' }) +
        " drag=" + $(if ([Math]::Abs($dx) -ge 60 -and [Math]::Abs($dy) -ge 40) { 'ok' } else { 'FAIL' }))
} else { Note '[=] could not read the rectangle before/after' }

Write-Output '===== 7. evidence ====='
foreach ($f in @('read.log', 'mirror-view.log')) {
    $p = Join-Path $dir $f
    if (Test-Path $p) { Note ("--- " + $f + " ---"); Get-Content $p -Tail 6 | ForEach-Object { Note ("  " + $_) } }
}
Get-Process wsm -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Stop-Process -Id $read.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Get-Process cmd -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*$filter*" } | Stop-Process -Force -ErrorAction SilentlyContinue
