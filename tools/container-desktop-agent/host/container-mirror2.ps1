# container-mirror2.ps1 - runs ELEVATED.
# The container-side end-to-end: a window INSIDE the container -> mirrored on the host
# -> dragged -> the container's window moves.
$ErrorActionPreference = 'Continue'
$dd = 'C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd'
$repo = 'C:\Users\User\Documents\WindowsSandboxManager'
$wsm = "$repo\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe"
$state = Get-Content (Join-Path $dd 'state.json') -Raw | ConvertFrom-Json
$cs = $state.id
$root = $state.mount
$mirror = [System.IO.Path]::Combine($root, 'wsm\mirror.shm')
$viewLog = [System.IO.Path]::Combine($root, 'wsm\mirror-view.log')
$readLog = [System.IO.Path]::Combine($root, 'wsm\read.log')
Write-Output "container $cs root=$root"

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class Cm {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
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

Write-Output ''
Write-Output '===== 0. stop any previous read side and clear its output ====='
$wsmDir = [System.IO.Path]::Combine($root, 'wsm')
[System.IO.File]::WriteAllText([System.IO.Path]::Combine($wsmDir, 'agent-stop.txt'), 'stop')
Start-Sleep -Seconds 4
foreach ($f in @('agent-stop.txt', 'mirror.shm', 'read.log', 'read.out', 'mirror-view.log')) {
    $p = [System.IO.Path]::Combine($wsmDir, $f)
    if ([System.IO.File]::Exists($p)) { [System.IO.File]::Delete($p) }
}
Write-Output '  cleared'

Write-Output ''
Write-Output '===== 1. start a window INSIDE the container (service session) ====='
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dd 'q.ps1') -Line 'cmd.exe /c start notepad' -TimeoutSec 200 2>&1 | Select-Object -Last 2
Start-Sleep -Seconds 6

Write-Output ''
Write-Output '===== 2. provider start (materialise + HCS launch) + present ====='
& $wsm desktop --compute-system $cs --container-root $root --provider window-mirror
Write-Output ("exit=" + $LASTEXITCODE)
Start-Sleep -Seconds 6

Write-Output ''
Write-Output '===== 3. mirror windows on the host ====='
$mirrorInfo = [Cm]::FindMirror('Notepad')
Write-Output ("  mirror window: " + $(if ($mirrorInfo) { $mirrorInfo } else { '(none)' }))

Write-Output ''
Write-Output '===== 4. drag the mirrored window by (+90,+60) ====='
if ($mirrorInfo -match 'hwnd=0x([0-9A-F]+)') {
    $hwnd = [IntPtr][Convert]::ToInt64($Matches[1], 16)
    [void][Cm]::PostMessage($hwnd, 0x0201, [Cm]::LeftButton, [Cm]::Pt(60, 12))
    Start-Sleep -Milliseconds 250
    foreach ($step in 1..6) {
        [void][Cm]::PostMessage($hwnd, 0x0200, [Cm]::LeftButton, [Cm]::Pt((60 + $step * 15), (12 + $step * 10)))
        Start-Sleep -Milliseconds 160
    }
    [void][Cm]::PostMessage($hwnd, 0x0202, [IntPtr]::Zero, [Cm]::Pt(150, 72))
    Start-Sleep -Seconds 5
    Write-Output '  drag posted'
} else {
    Write-Output '  no mirror window to drag'
}

Write-Output ''
Write-Output '===== 5. the container window''s published rect after the drag ====='
& $wsm desktop --compute-system $cs --container-root $root --provider window-mirror --no-present
Write-Output ("exit=" + $LASTEXITCODE)

Write-Output ''
Write-Output '===== 6. evidence ====='
foreach ($f in @($readLog, $viewLog)) {
    if ([System.IO.File]::Exists($f)) {
        Write-Output ("--- " + [System.IO.Path]::GetFileName($f) + " (tail) ---")
        Get-Content $f -Tail 12 | ForEach-Object { Write-Output ("  " + $_) }
    } else {
        Write-Output ("--- " + $f + " (missing)")
    }
}

Write-Output ''
Write-Output '===== 7. cleanup ====='
Get-Process wsm -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -like '*Notepad*' } | Stop-Process -Force -ErrorAction SilentlyContinue
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dd 'q.ps1') -Line 'cmd.exe /c taskkill /f /im notepad.exe' -TimeoutSec 120 2>&1 | Select-Object -Last 1
