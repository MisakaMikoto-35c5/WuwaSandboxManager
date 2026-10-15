# elevtest.ps1 (HOST, meant to run ELEVATED) - test the two claims that decide the
# design for presenting a container's desktop:
#
#   A. handle pass-through: can this elevated host OpenProcess() a container pid
#      and DuplicateHandle() a section out of it, then MapViewOfFile() it?
#      (This is what would make a page-file section - pure RAM, no disk - work.)
#   B. window injection: can the host see/enumerate the container's windows at
#      all?  (FindWindow/EnumWindows only see the calling desktop.)
$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class T {
    [StructLayout(LayoutKind.Sequential)] public struct LUID_AND_ATTRIBUTES { public long Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] public struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privilege; }

    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool DuplicateHandle(IntPtr srcProc, IntPtr srcHandle,
        IntPtr dstProc, out IntPtr dstHandle, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint offHi, uint offLo, ulong length);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool LookupPrivilegeValueW(string system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool EnumWindows(EnumProc cb, IntPtr param);
    public delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetUserObjectInformationW(IntPtr h, int index, StringBuilder info, int len, out int needed);

    public static string EnableDebug() {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) return "OpenProcessToken err " + Marshal.GetLastWin32Error();
        long luid;
        if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out luid)) return "LookupPrivilegeValue err " + Marshal.GetLastWin32Error();
        TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
        tp.PrivilegeCount = 1; tp.Privilege.Luid = luid; tp.Privilege.Attributes = 0x2;
        if (!AdjustTokenPrivileges(token, false, ref tp, (uint)Marshal.SizeOf(typeof(TOKEN_PRIVILEGES)), IntPtr.Zero, IntPtr.Zero))
            return "AdjustTokenPrivileges err " + Marshal.GetLastWin32Error();
        int e = Marshal.GetLastWin32Error();
        return e == 0 ? "SeDebugPrivilege ENABLED" : "AdjustTokenPrivileges err " + e + " (1300 = privilege not held)";
    }
    public static string ReadAscii(IntPtr p, int len) {
        byte[] b = new byte[len]; Marshal.Copy(p, b, 0, len); return Encoding.ASCII.GetString(b).TrimEnd('\0');
    }
    public static string EnumTitles() {
        StringBuilder sb = new StringBuilder();
        EnumWindows(delegate(IntPtr h, IntPtr p) {
            StringBuilder t = new StringBuilder(128);
            GetWindowTextW(h, t, 128);
            if (t.Length > 0) sb.Append("[").Append(t.ToString()).Append("] ");
            return true;
        }, IntPtr.Zero);
        return sb.ToString();
    }
}
'@
$m = [Runtime.InteropServices.Marshal]

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Output ("host identity: {0}  elevated(Administrators)={1}" -f $id.Name, $isAdmin)
Write-Output ("debug privilege: {0}" -f [T]::EnableDebug())

# ---------------------------------------------------------------- B: windows
Write-Output '==== B. can this host see the container''s windows? ===='
$hwnd = [T]::FindWindowW($null, 'Registry Editor')
Write-Output ("  FindWindow(null, 'Registry Editor') = 0x{0:X}  (0 = not visible from this desktop)" -f $hwnd.ToInt64())
$titles = [T]::EnumTitles()
Write-Output ("  EnumWindows titles on THIS desktop: " + $titles.Substring(0, [Math]::Min(400, $titles.Length)))
$idesk = [T]::OpenInputDesktop(0, $false, 0x0100)
if ($idesk -ne [IntPtr]::Zero) {
    $sb = New-Object Text.StringBuilder 256
    $need = 0
    [void][T]::GetUserObjectInformationW($idesk, 2, $sb, 512, [ref]$need)
    Write-Output ("  host input desktop handle=0x{0:X} name='{1}'" -f $idesk.ToInt64(), $sb.ToString())
    [void][T]::CloseHandle($idesk)
}
else { Write-Output ('  OpenInputDesktop failed err=' + $m::GetLastWin32Error()) }

# ------------------------------------------------------- A: handle pass-through
Write-Output '==== A. handle pass-through from the container ===='
$mount = (Get-Content "$env:TEMP\opencode\wsmdiag\dd\mount.txt" -Raw).Trim()
$handlesFile = $mount.TrimEnd('\') + '\wsm\shm-handles.txt'
for ($i = 0; $i -lt 180 -and -not (Test-Path $handlesFile); $i++) { Start-Sleep -Milliseconds 1000 }
if (-not (Test-Path $handlesFile)) { Write-Output "  MISSING $handlesFile"; return }
$txt = Get-Content $handlesFile -Raw
Write-Output ('  container published: ' + ($txt -replace "`r?`n", ' | '))
$cpid = [int](($txt -split "`r?`n" | Where-Object { $_ -match '^pid=' }) -replace '^pid=', '')
$hUnnamed = [Int64](($txt -split "`r?`n" | Where-Object { $_ -match '^handle_unnamed=' }) -replace '^handle_unnamed=', '')
$hFrame = [Int64](($txt -split "`r?`n" | Where-Object { $_ -match '^handle_frame=' }) -replace '^handle_frame=', '')
$cap = [int](($txt -split "`r?`n" | Where-Object { $_ -match '^frame_capacity=' }) -replace '^frame_capacity=', '')
Write-Output ("  container pid={0} unnamed=0x{1:X} frame=0x{2:X} frameCap={3}" -f $cpid, $hUnnamed, $hFrame, $cap)

$p = Get-Process -Id $cpid -ErrorAction SilentlyContinue
Write-Output ("  Get-Process -Id {0}: {1}" -f $cpid, $(if ($p) { "name=$($p.ProcessName) session=$($p.SessionId)" } else { 'NOT VISIBLE' }))

$hp = [T]::OpenProcess(0x0040, $false, $cpid)
Write-Output ("  OpenProcess(PROCESS_DUP_HANDLE) -> 0x{0:X} err={1}" -f $hp.ToInt64(), $m::GetLastWin32Error())
if ($hp -ne [IntPtr]::Zero) {
    foreach ($pair in @(@($hUnnamed, 'unnamed', 131072, 'WSM-UNNAMED-OK'), @($hFrame, 'frame', $cap, 'WSM-FRAME-OK'))) {
        $dup = [IntPtr]::Zero
        $ok = [T]::DuplicateHandle($hp, [IntPtr]$pair[0], [T]::GetCurrentProcess(), [ref]$dup, 0, $false, 0x0002)
        Write-Output ("  DuplicateHandle({0}) ok={1} dup=0x{2:X} err={3}" -f $pair[1], $ok, $dup.ToInt64(), $m::GetLastWin32Error())
        if ($ok -and $dup -ne [IntPtr]::Zero) {
            $view = [T]::MapViewOfFile($dup, 0x0004, 0, 0, [uint64]$pair[2])
            if ($view -ne [IntPtr]::Zero) {
                $head = [T]::ReadAscii($view, 32)
                $tail = [T]::ReadAscii([IntPtr]::Add($view, [int]$pair[2] - 64), 32)
                Write-Output ("    MAP OK ({0} bytes): head='{1}' tail='{2}'" -f $pair[2], $head, $tail)
                [void][T]::UnmapViewOfFile($view)
            }
            else { Write-Output ('    MapViewOfFile FAILED err=' + $m::GetLastWin32Error()) }
            [void][T]::CloseHandle($dup)
        }
    }
    [void][T]::CloseHandle($hp)
}
Write-Output '==== done ===='
