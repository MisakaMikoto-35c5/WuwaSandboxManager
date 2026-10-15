# launch-in-console.ps1 - start a process IN THE INTERACTIVE CONSOLE SESSION.
#
# This is the mechanism behind "a SYSTEM service shows a window on the user's
# desktop": query the console session's user token and hand it to
# CreateProcessAsUser with lpDesktop = winsta0\default.  It needs SeTcbPrivilege
# (SYSTEM only) - an UAC-elevated admin token does not have it, so run this as
# SYSTEM (e.g. a scheduled task with /ru SYSTEM).
#
#   powershell -File launch-in-console.ps1 -Arguments "-File C:\...\deskview.ps1" -Log out.txt
param(
    [string] $Exe = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe",
    [string] $Arguments = '',
    [string] $WorkingDirectory = "$env:SystemRoot\System32",
    [int64] $SessionId = -1,              # -1 = the active console session
    [string] $PayloadFile = '',           # a .ps1 to run *in* the console session
    [string] $Log = "$env:TEMP\launch-in-console.log"
)

$ErrorActionPreference = 'Continue'
if ($PayloadFile) {
    # Compose the command line here so no caller has to get cmd.exe quoting right.
    $Exe = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PayloadFile + '"'
}
function Log([string] $m) {
    $line = ('{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $m)
    Write-Output $line
    try { Add-Content -Path $Log -Value $line -ErrorAction SilentlyContinue } catch { }
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class L {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] public struct LUID_AND_ATTRIBUTES { public long Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] public struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privilege; }

    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ProcessIdToSessionId(uint pid, out uint sid);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcessId();
    [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int level, int type, out IntPtr newToken);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessAsUserW(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("userenv.dll", SetLastError = true)] public static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] public static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool LookupPrivilegeValueW(string system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);

    public static string Enable(string name) {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x28, out token)) return name + ": OpenProcessToken err " + Marshal.GetLastWin32Error();
        long luid;
        if (!LookupPrivilegeValueW(null, name, out luid)) return name + ": LookupPrivilegeValue err " + Marshal.GetLastWin32Error();
        TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
        tp.PrivilegeCount = 1; tp.Privilege.Luid = luid; tp.Privilege.Attributes = 0x2;
        if (!AdjustTokenPrivileges(token, false, ref tp, (uint)Marshal.SizeOf(typeof(TOKEN_PRIVILEGES)), IntPtr.Zero, IntPtr.Zero))
            return name + ": AdjustTokenPrivileges err " + Marshal.GetLastWin32Error();
        int e = Marshal.GetLastWin32Error();
        return name + ": " + (e == 0 ? "enabled" : "err " + e + " (1300 = not held)");
    }
}
'@

$m = [Runtime.InteropServices.Marshal]
Write-Output ('identity: ' + [Security.Principal.WindowsIdentity]::GetCurrent().Name)
foreach ($p in 'SeTcbPrivilege', 'SeAssignPrimaryTokenPrivilege', 'SeIncreaseQuotaPrivilege', 'SeDebugPrivilege') {
    Write-Output ('  ' + [L]::Enable($p))
}

$own = 0
[void][L]::ProcessIdToSessionId([L]::GetCurrentProcessId(), [ref]$own)
$console = [L]::WTSGetActiveConsoleSessionId()
Log ("own pid=$([L]::GetCurrentProcessId()) session=$own ; active console session=$console")
if ($SessionId -lt 0) { $SessionId = $console }

$token = [IntPtr]::Zero
$ok = [L]::WTSQueryUserToken([uint32]$SessionId, [ref]$token)
Log ("WTSQueryUserToken($SessionId) ok=$ok token=0x{0:X} err={1}" -f $token.ToInt64(), $m::GetLastWin32Error())
if (-not $ok) { Log 'cannot get the console user token - nothing else to try'; return }

$primary = [IntPtr]::Zero
$ok = [L]::DuplicateTokenEx($token, 0x02000000, [IntPtr]::Zero, 2, 1, [ref]$primary)   # MAXIMUM_ALLOWED, SecurityImpersonation, TokenPrimary
Log ("DuplicateTokenEx ok=$ok primary=0x{0:X} err={1}" -f $primary.ToInt64(), $m::GetLastWin32Error())
if (-not $ok) { return }

$env = [IntPtr]::Zero
[void][L]::CreateEnvironmentBlock([ref]$env, $primary, $false)

$si = New-Object L+STARTUPINFO
$si.cb = $m::SizeOf($si)
$si.lpDesktop = 'winsta0\default'          # the interactive desktop of that session
$si.lpTitle = ''
$pi = New-Object L+PROCESS_INFORMATION
$cmd = ('"{0}" {1}' -f $Exe, $Arguments)
Log ("CreateProcessAsUser: $cmd  (winsta0\default, session $SessionId)")
$ok = [L]::CreateProcessAsUserW($primary, $Exe, $cmd, [IntPtr]::Zero, [IntPtr]::Zero, $false, 0x00000400, $env, $WorkingDirectory, [ref]$si, [ref]$pi)
Log ("CreateProcessAsUser ok=$ok pid=$($pi.dwProcessId) err={0}" -f $m::GetLastWin32Error())
if ($ok) {
    $child = 0
    [void][L]::ProcessIdToSessionId([uint32]$pi.dwProcessId, [ref]$child)
    Log ("child pid=$($pi.dwProcessId) session=$child  (should equal $SessionId)")
    [void][L]::CloseHandle($pi.hProcess); [void][L]::CloseHandle($pi.hThread)
}
if ($env -ne [IntPtr]::Zero) { [void][L]::DestroyEnvironmentBlock($env) }
[void][L]::CloseHandle($primary); [void][L]::CloseHandle($token)
