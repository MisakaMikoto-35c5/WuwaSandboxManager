# mirror-drag.ps1 - display + drag test (v4).
# v4: the drag is driven by posting real mouse messages to the mirror window (no
# physical cursor, no DPI ambiguity), which is exactly what the viewer's drag math now
# consumes.
$ErrorActionPreference = 'Continue'
$root = "$env:TEMP\opencode\wsmdiag\mirror-test4"
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $root -Force | Out-Null

$repo = 'C:\Users\User\Documents\WindowsSandboxManager'
Copy-Item "$repo\src\10-Hcs\Wsm.Hcs.Storage\Images\wsm-desktop-read.ps1" "$root\wsm-desktop-read.ps1" -Force
$wsm = "$repo\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe"
$mirror = "$root\mirror.shm"
$filter = 'wsm-mirror-target'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WsmT4 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder t, int m);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
    public static readonly IntPtr LeftButton = (IntPtr)0x0001;
    public static IntPtr Point(int x, int y) { return (IntPtr)((y << 16) | (x & 0xFFFF)); }
    delegate bool Proc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(Proc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    public static List<string> Matching(string needle) {
        var found = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(300); GetWindowTextW(h, sb, sb.Capacity);
            if (sb.ToString().IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) return true;
            RECT r; GetWindowRect(h, out r);
            int pid; GetWindowThreadProcessId(h, out pid);
            found.Add(string.Format("hwnd=0x{0:X} pid={1} rect=({2},{3} {4}x{5}) \"{6}\"",
                h.ToInt64(), pid, r.L, r.T, r.R - r.L, r.B - r.T, sb.ToString()));
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
function Note([string] $m) { Write-Output $m }

# ---- 1. target window ------------------------------------------------------------
$target = Start-Process cmd.exe -PassThru -ArgumentList @('/k', "title $filter && echo window mirror drag test")
Start-Sleep -Seconds 3
$targetHwnd = [IntPtr]::Zero
$first = [WsmT4]::Matching($filter) | Select-Object -First 1
if ($first -match 'hwnd=0x([0-9A-F]+)') { $targetHwnd = [IntPtr][Convert]::ToInt64($Matches[1], 16) }
[void][WsmT4]::SetWindowPos($targetHwnd, [IntPtr]::Zero, 80, 80, 420, 300, 0x0004)
Start-Sleep -Milliseconds 900
$before = New-Object WsmT4+RECT
[void][WsmT4]::GetWindowRect($targetHwnd, [ref]$before)
Note ("[1] target hwnd=0x" + $targetHwnd.ToInt64().ToString('X') + " rect before=(" + $before.L + "," + $before.T + ")")

# ---- 2. read side ----------------------------------------------------------------
$read = Start-Process powershell.exe -PassThru -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass',
    '-File', "$root\wsm-desktop-read.ps1", '-AgentDirectory', $root, '-MirrorFile', 'mirror.shm',
    '-InputPrefix', 'input-', '-StopFile', 'agent-stop.txt', '-Width', $screen.Width, '-Height', $screen.Height,
    '-Fps', 4, '-Seconds', 180, '-TitleFilter', $filter)
for ($i = 0; $i -lt 40 -and -not (Test-Path $mirror); $i++) { Start-Sleep -Milliseconds 500 }
Note ("[2] read side pid=" + $read.Id + " mirror.shm=" + (Test-Path $mirror))

# ---- 3. viewer (offset so the mirror does not sit on the real window) ------------
$offsetX = 420; $offsetY = 240
$view = Start-Process $wsm -PassThru -ArgumentList @('desktop-view','--mode','windows','--frame',$mirror,
    '--seconds','90','--input-prefix','input-','--offset-x',$offsetX,'--offset-y',$offsetY)
Start-Sleep -Seconds 6
$mirrorHwnd = [IntPtr]::Zero
$mirrorRect = $null
foreach ($m in [WsmT4]::Matching($filter)) {
    if ($m -match ("pid=" + $view.Id + " ") -and $m -match 'hwnd=0x([0-9A-F]+).*rect=\((-?\d+),(-?\d+) (\d+)x(\d+)\)') {
        $mirrorHwnd = [IntPtr][Convert]::ToInt64($Matches[1], 16)
        $mirrorRect = @{ X = [int]$Matches[2]; Y = [int]$Matches[3]; W = [int]$Matches[4]; H = [int]$Matches[5] }
    }
}
$viewLog = "$root\mirror-view.log"
if (Test-Path $viewLog) { Note ("[3] log: " + ((Select-String -Path $viewLog -Pattern 'window id=0x' | Select-Object -First 1).Line -replace '^\S+\s+','')) }
Note ("[3] DISPLAY: mirror window hwnd=0x" + $mirrorHwnd.ToInt64().ToString('X') + " rect=(" + $mirrorRect.X + "," + $mirrorRect.Y + " " + $mirrorRect.W + "x" + $mirrorRect.H + ")")

# ---- 4. drive a drag through the mirror window's messages -----------------------
$dx = 90; $dy = 60
[void][WsmT4]::PostMessage($mirrorHwnd, 0x0201, [WsmT4]::LeftButton, [WsmT4]::Point(60, 12))  # WM_LBUTTONDOWN in the title band
Start-Sleep -Milliseconds 200
foreach ($step in 1..6) {
    [void][WsmT4]::PostMessage($mirrorHwnd, 0x0200, [WsmT4]::LeftButton,
        [WsmT4]::Point((60 + [int]($dx * $step / 6)), (12 + [int]($dy * $step / 6))))               # WM_MOUSEMOVE (captured)
    Start-Sleep -Milliseconds 150
}
[void][WsmT4]::PostMessage($mirrorHwnd, 0x0202, [IntPtr]::Zero, [WsmT4]::Point(60 + $dx, 12 + $dy)) # WM_LBUTTONUP
Start-Sleep -Seconds 4
Note ("[4] dragged the mirror by (+" + $dx + ",+" + $dy + ") via posted mouse messages")

# ---- 5. results -----------------------------------------------------------------
$after = New-Object WsmT4+RECT
[void][WsmT4]::GetWindowRect($targetHwnd, [ref]$after)
$movedX = $after.L - $before.L; $movedY = $after.T - $before.T
Note ("[5] target rect after=(" + $after.L + "," + $after.T + ") delta=(" + $movedX + "," + $movedY + ")")
$dragLines = @(Select-String -Path $viewLog -Pattern 'drag (begin|move|end)' -ErrorAction SilentlyContinue)
Note ("[6] viewer drag lines=" + $dragLines.Count)
$dragLines | Select-Object -First 3 | ForEach-Object { Note ("      " + ($_.Line -replace '^\S+\s+','')) }
$moveLines = @(Select-String -Path "$root\read.log" -Pattern 'movewindow' -ErrorAction SilentlyContinue)
Note ("[7] read side applied=" + $(if ($moveLines.Count) { ($moveLines[-1].Line -replace '^\S+\s+','') } else { '(none)' }))
$ok = ($dragLines.Count -gt 0) -and ($moveLines.Count -gt 0) -and ($movedX -ge ($dx - 20)) -and ($movedY -ge ($dy - 20))
Note ("[=] VERDICT display=" + $(if ($mirrorHwnd -ne [IntPtr]::Zero) { 'ok' } else { 'fail' }) + " drag=" + $(if ($ok) { 'ok' } else { 'fail' }))

Stop-Process -Id $view.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $read.Id -Force -ErrorAction SilentlyContinue
Stop-Process -Id $target.Id -Force -ErrorAction SilentlyContinue
Get-Process cmd -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like "*$filter*" } | Stop-Process -Force -ErrorAction SilentlyContinue
