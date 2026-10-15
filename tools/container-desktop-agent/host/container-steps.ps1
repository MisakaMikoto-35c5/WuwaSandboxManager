# container-steps.ps1 - container-side verification in MINIMAL, individually runnable steps.
#
#   powershell -File container-steps.ps1 -Step 1      (needs elevation: starts the runner)
#   powershell -File container-steps.ps1 -Step 2      (no elevation)
#   ... 3,4,5,6,7                                      (no elevation)
#   powershell -File container-steps.ps1 -Step 8      (needs elevation: the product path)
#
# Each step does ONE thing, has bounded waits, and prints [STEP n] PASS/FAIL with the raw
# evidence that decides it.
param([Parameter(Mandatory = $true)][int] $Step)

$ErrorActionPreference = 'Continue'
$dd = "$env:TEMP\opencode\wsmdiag\dd"
$repo = 'C:\Users\User\Documents\WindowsSandboxManager'
$wsm = "$repo\src\50-App\Wsm.Cli\bin\Debug\net10.0-windows\wsm.exe"
$o = Join-Path $dd 'o.txt'

function Ok([string] $m) { Write-Output ("[STEP $Step] PASS: " + $m); exit 0 }
function Bad([string] $m) { Write-Output ("[STEP $Step] FAIL: " + $m); exit 1 }
function WsmRoot { (Get-Content (Join-Path $dd 'state.json') -Raw | ConvertFrom-Json).mount }
function WsmDir { [System.IO.Path]::Combine((WsmRoot), 'wsm') }
function Run-In([string] $line) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dd 'q.ps1') -Line $line -TimeoutSec 120 2>&1 | Out-Null
}
function Runner-Tail([int] $n = 25) { Get-Content $o -Tail $n -ErrorAction SilentlyContinue }

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
public static class Cs {
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
            string name = ""; try { name = Process.GetProcessById(pid).ProcessName; } catch (Exception) { }
            if (!string.Equals(name, "wsm", StringComparison.OrdinalIgnoreCase)) return true;
            found = h; detail = string.Format("hwnd=0x{0:X} pid={1} \"{2}\"", h.ToInt64(), pid, sb.ToString());
            return false;
        }, IntPtr.Zero);
        return found == IntPtr.Zero ? "" : detail;
    }
}
'@

switch ($Step) {

    1 {
        Write-Output 'STEP 1: start the HCS runner and one container (elevated), then prove it can run a process'
        Remove-Item (Join-Path $dd 'state.json') -Force -ErrorAction SilentlyContinue
        Remove-Item $o -Force -ErrorAction SilentlyContinue
        Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $dd 'host.ps1')) | Out-Null
        $mount = $null
        for ($i = 0; $i -lt 80; $i++) {
            Start-Sleep -Seconds 3
            if (Test-Path (Join-Path $dd 'state.json')) {
                $s = Get-Content (Join-Path $dd 'state.json') -Raw | ConvertFrom-Json
                if ($s.mount) { $mount = $s.mount; $ip = $s.ip; break }
            }
        }
        if (-not $mount) { Runner-Tail 15 | ForEach-Object { Write-Output ("  " + $_) }; Bad 'the runner never produced a container' }
        Write-Output ("  container=" + $s.id + "  ip=" + $ip)
        Write-Output ("  root=" + $mount)
        Run-In 'cmd.exe /c echo ALIVE'
        Start-Sleep -Seconds 3
        $tail = (Runner-Tail 30) -join "`n"
        if ($tail -notmatch 'ALIVE') { Runner-Tail 15 | ForEach-Object { Write-Output ("  " + $_) }; Bad 'the container cannot run a process (see o.txt)' }
        Ok 'container up and able to run processes'
    }

    2 {
        Write-Output 'STEP 2: stage the current read side into the container root (no elevation needed)'
        $dir = WsmDir
        $ok = $true
        try {
            [System.IO.File]::Copy("$repo\src\10-Hcs\Wsm.Hcs.Storage\Images\wsm-desktop-read.ps1", [System.IO.Path]::Combine($dir, 'wsm-desktop-read.ps1'), $true)
        } catch { Write-Output ("  copy ps1 failed: " + $_.Exception.Message); $ok = $false }
        try {
            [System.IO.File]::Copy("$repo\src\10-Hcs\Wsm.Hcs.Storage\Images\wsm-desktop-read.cmd", [System.IO.Path]::Combine($dir, 'wsm-desktop-read.cmd'), $true)
        } catch { Write-Output ("  copy cmd failed: " + $_.Exception.Message); $ok = $false }
        if (-not $ok) { Bad 'staging failed' }
        foreach ($f in @('agent-stop.txt','mirror.shm','read.log','read.out','mirror-view.log')) {
            $p = [System.IO.Path]::Combine($dir, $f)
            if ([System.IO.File]::Exists($p)) { [System.IO.File]::Delete($p) }
        }
        $p = [System.IO.Path]::Combine($dir, 'wsm-desktop-read.ps1')
        if (-not [System.IO.File]::Exists($p)) { Bad 'the read side is not visible in the container root' }
        $size = ([System.IO.FileInfo]::new($p)).Length
        if ($size -lt 20000) { Bad ("the staged read side looks wrong: $size bytes") }
        Ok ("read side staged and readable ($size bytes at " + $dir + ")")
    }

    3 {
        Write-Output 'STEP 3: start a window process inside the container and confirm it exists'
        Run-In 'cmd.exe /c start /b notepad'
        Start-Sleep -Seconds 5
        Run-In 'cmd.exe /c tasklist /fi "imagename eq notepad.exe"'
        Start-Sleep -Seconds 3
        $tail = (Runner-Tail 30) -join "`n"
        ($tail -split "`n") | Where-Object { $_ -match 'notepad' } | Select-Object -First 3 | ForEach-Object { Write-Output ("  " + $_.Trim()) }
        if ($tail -notmatch 'notepad\.exe') { Runner-Tail 20 | ForEach-Object { Write-Output ("  " + $_) }; Bad 'no notepad inside the container' }
        Ok 'a GUI process is running inside the container'
    }

    4 {
        Write-Output 'STEP 4: start the read side inside the container and read what it reports'
        Run-In 'cmd.exe /c C:\wsm\wsm-winlogon-wrap.cmd'
        $log = [System.IO.Path]::Combine((WsmDir), 'read.log')
        for ($i = 0; $i -lt 30 -and -not [System.IO.File]::Exists($log); $i++) { Start-Sleep -Seconds 2 }
        if (-not [System.IO.File]::Exists($log)) {
            $out = [System.IO.Path]::Combine((WsmDir), 'read.out')
            if ([System.IO.File]::Exists($out)) { Get-Content $out -Tail 15 | ForEach-Object { Write-Output ("  " + $_) } }
            Bad 'the read side produced no read.log (see read.out above)'
        }
        $lines = Get-Content $log
        $lines | Select-Object -First 8 | ForEach-Object { Write-Output ("  " + $_) }
        if (-not ($lines -match 'starting pid')) { Bad 'read.log has no startup line' }
        if (-not ($lines -match 'session: winsta=')) { Bad 'read.log has no session line' }
        if (-not ($lines -match 'DIB section over shared memory ready')) { Bad 'the shared DIB / memory DC step failed (see read.log)' }
        if (-not ($lines -match 'read side started')) { Bad 'the read side did not reach its loop' }
        Ok 'the read side runs inside the container'
    }

    5 {
        Write-Output 'STEP 5: is a window published, and did PrintWindow give content?'
        $mirror = [System.IO.Path]::Combine((WsmDir), 'mirror.shm')
        for ($i = 0; $i -lt 30 -and -not [System.IO.File]::Exists($mirror); $i++) { Start-Sleep -Seconds 2 }
        if (-not [System.IO.File]::Exists($mirror)) { Bad 'mirror.shm was never published' }
        $table = & $wsm desktop-view --frame $mirror --print-table 2>&1
        $table | ForEach-Object { Write-Output ("  " + $_) }
        Get-Content ([System.IO.Path]::Combine((WsmDir), 'read.log')) | Select-String -Pattern 'first paint|paint failed|session:' | Select-Object -First 8 | ForEach-Object { Write-Output ("  " + $_.Line) }
        if (-not (($table -join "`n") -match 'Windows  : [1-9]')) { Bad 'the published window table is empty' }
        $log = (Get-Content ([System.IO.Path]::Combine((WsmDir), 'read.log'))) -join "`n"
        if ($log -match 'paint failed') { Bad 'PrintWindow failed for a window (see read.log)' }
        if ($log -match 'ink=0') { Bad 'PrintWindow returned no pixels (ink=0%) - the window was never painted in that session' }
        Ok 'a container window is published with real pixels'
    }

    6 {
        Write-Output 'STEP 6: show the container window on the host as a real window'
        $mirror = [System.IO.Path]::Combine((WsmDir), 'mirror.shm')
        $title = 'Notepad'
        Start-Process $wsm -ArgumentList @('desktop-view','--mode','windows','--frame',$mirror,
            '--seconds','150','--offset-x','420','--offset-y','240') | Out-Null
        $info = ''
        for ($i = 0; $i -lt 12 -and -not $info; $i++) { Start-Sleep -Seconds 2; $info = [Cs]::FindMirror($title) }
        if (-not $info) { Bad 'no mirror window appeared on the host' }
        Write-Output ("  mirror window: " + $info)
        Ok 'the container window is displayed on the host'
    }

    7 {
        Write-Output 'STEP 7: drag the mirrored window and check the container window really moved'
        $mirror = [System.IO.Path]::Combine((WsmDir), 'mirror.shm')
        $log = [System.IO.Path]::Combine((WsmDir), 'read.log')
        $info = [Cs]::FindMirror('Notepad')
        if (-not $info) { Bad 'no mirror window to drag (run step 6 first)' }
        $before = (& $wsm desktop-view --frame $mirror --print-table 2>&1) -join "`n"
        $bx = $null; $by = $null
        if ($before -match 'id=0x[0-9A-F]+ rect=\((-?\d+),(-?\d+)') { $bx = [int]$Matches[1]; $by = [int]$Matches[2] }
        $hwnd = [IntPtr][Convert]::ToInt64([regex]::Match($info, 'hwnd=0x([0-9A-F]+)').Groups[1].Value, 16)
        [void][Cs]::PostMessage($hwnd, 0x0201, [Cs]::LeftButton, [Cs]::Pt(60, 12))
        Start-Sleep -Milliseconds 250
        foreach ($step in 1..6) {
            [void][Cs]::PostMessage($hwnd, 0x0200, [Cs]::LeftButton, [Cs]::Pt((60 + $step * 15), (12 + $step * 10)))
            Start-Sleep -Milliseconds 170
        }
        [void][Cs]::PostMessage($hwnd, 0x0202, [IntPtr]::Zero, [Cs]::Pt(150, 72))
        Start-Sleep -Seconds 6
        Get-Content $log | Select-String -Pattern 'movewindow' | Select-Object -Last 3 | ForEach-Object { Write-Output ("  " + $_.Line) }
        $after = (& $wsm desktop-view --frame $mirror --print-table 2>&1) -join "`n"
        ($after -split "`n") | Where-Object { $_ -match 'id=0x' } | ForEach-Object { Write-Output ("  " + $_.Trim()) }
        $ax = $null
        if ($after -match 'id=0x[0-9A-F]+ rect=\((-?\d+),(-?\d+)') { $ax = [int]$Matches[1] }
        if ($null -eq $bx -or $null -eq $ax) { Bad 'could not read the window rectangle before/after' }
        if (($ax - $bx) -lt 60) { Bad ("the container window did not move (x $bx -> $ax)") }
        Ok ("dragging moved the container window by (" + ($ax - $bx) + "," + (([int]([regex]::Match($after, 'rect=\(-?\d+,(-?\d+)').Groups[1].Value)) - $by) + ")")
    }

    8 {
        Write-Output 'STEP 8: integrated - the product path (provider window-mirror, launch + present)'
        $s = Get-Content (Join-Path $dd 'state.json') -Raw | ConvertFrom-Json
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $dd 'q.ps1') -Line 'cmd.exe /c taskkill /f /im notepad.exe' -TimeoutSec 120 2>&1 | Out-Null
        Run-In 'cmd.exe /c start /b notepad'
        Start-Sleep -Seconds 5
        [System.IO.File]::WriteAllText([System.IO.Path]::Combine((WsmDir), 'agent-stop.txt'), 'stop')
        Start-Sleep -Seconds 4
        foreach ($f in @('agent-stop.txt','mirror.shm','read.log','read.out')) {
            $p = [System.IO.Path]::Combine((WsmDir), $f)
            if ([System.IO.File]::Exists($p)) { [System.IO.File]::Delete($p) }
        }
        & $wsm desktop --compute-system $s.id --container-root $s.mount --provider window-mirror
        $code = $LASTEXITCODE
        $mirror = [System.IO.Path]::Combine((WsmDir), 'mirror.shm')
        Start-Sleep -Seconds 6
        $info = [Cs]::FindMirror('Notepad')
        Write-Output ("  exit=" + $code + "  mirror.shm=" + [System.IO.File]::Exists($mirror) + "  mirrorWindow=" + $(if ($info) { $info } else { '(none)' }))
        if ($code -ne 0) { Bad 'the provider path failed (see the error above)' }
        if (-not [System.IO.File]::Exists($mirror)) { Bad 'the provider did not get a published mirror' }
        if (-not $info) { Bad 'the provider started but no mirror window appeared' }
        Ok 'the product path works end to end'
    }

    default { Bad ("unknown step " + $Step) }
}
