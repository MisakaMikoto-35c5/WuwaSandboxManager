# winsta-spawn4.ps1 - runs as SYSTEM. Why do children in the container's session die?
# Launches a few variants, waits, and reports pid/session/exit code/elapsed.
param(
    [int] $TargetSession = 4,
    [string] $OutDirectory = 'C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd',
    [string] $ContainerWsm = 'C:\wsm'
)

$ErrorActionPreference = 'Continue'
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class WinstaSpawn4
{
    const uint TOKEN_ALL_ACCESS = 0xF01FF;
    const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    const uint TOKEN_QUERY = 0x8;
    const uint MAXIMUM_ALLOWED = 0x02000000;
    const int  TokenSessionId = 12;
    const int  SecurityImpersonation = 2;
    const int  TokenPrimary = 1;
    const uint SE_PRIVILEGE_ENABLED = 0x2;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    const uint CREATE_NO_WINDOW = 0x08000000;
    const uint SYNCHRONIZE = 0x00100000;
    const uint PROCESS_QUERY_INFORMATION = 0x0400;

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    public static string Run(int session, string outDir, string containerWsm)
    {
        var log = new StringBuilder();
        IntPtr own = IntPtr.Zero, dup = IntPtr.Zero, sameSession = IntPtr.Zero;
        Enable(log, "SeTcbPrivilege"); Enable(log, "SeAssignPrimaryTokenPrivilege");
        Enable(log, "SeIncreaseQuotaPrivilege"); Enable(log, "SeDebugPrivilege");
        OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out own);
        DuplicateTokenEx(own, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out sameSession);
        DuplicateTokenEx(own, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dup);
        int sid = session;
        log.AppendLine("[t] SetTokenInformation(TokenSessionId=" + session + ") -> " + SetTokenInformation(dup, TokenSessionId, ref sid, 4));

        string cmdExe = @"C:\Windows\System32\cmd.exe";
        string ps = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

        log.AppendLine(RunOne("0-control-own-session", sameSession, "\"" + cmdExe + "\" /c exit 7", null, @"C:\Windows"));

        log.AppendLine(RunOne("1-s4-exit7", dup, "\"" + cmdExe + "\" /c exit 7", @"winsta0\default", @"C:\Windows"));
        log.AppendLine(RunOne("2-s4-nulldesk-exit7", dup, "\"" + cmdExe + "\" /c exit 7", null, @"C:\Windows"));
        log.AppendLine(RunOne("3-s4-write-container", dup, "\"" + cmdExe + "\" /c echo marker > " + containerWsm + "\\spawn1.txt", @"winsta0\default", @"C:\Windows"));
        log.AppendLine(RunOne("4-s4-ps-exit42", dup, "\"" + ps + "\" -NoProfile -Command \"exit 42\"", @"winsta0\default", @"C:\Windows"));
        string child = "\"" + ps + "\" -NoProfile -ExecutionPolicy Bypass -File \"" + containerWsm + "\\winsta-child.ps1\" -OutFile \"" + containerWsm + "\\winsta-child-v1.out\" -PngDirectory \"" + containerWsm + "\"";
        log.AppendLine(RunOne("5-s4-child-probe", dup, child, @"winsta0\default", @"C:\Windows"));

        return log.ToString();
    }

    static string RunOne(string tag, IntPtr token, string cmd, string desktop, string cwd)
    {
        IntPtr env = IntPtr.Zero;
        CreateEnvironmentBlock(out env, token, false);
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        si.lpDesktop = desktop;
        PROCESS_INFORMATION pi;
        var sw = Stopwatch.StartNew();
        bool ok = CreateProcessAsUser(token, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
            CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, cwd, ref si, out pi);
        int err = Marshal.GetLastWin32Error();
        string result;
        if (!ok)
        {
            result = string.Format("[{0}] CREATE FAILED err={1}", tag, err);
        }
        else
        {
            uint childSession = 0;
            ProcessIdToSessionId((uint)pi.dwProcessId, out childSession);
            WaitForSingleObject(pi.hProcess, 8000);
            uint code = 0xDEADBEEF;
            GetExitCodeProcess(pi.hProcess, out code);
            sw.Stop();
            result = string.Format("[{0}] pid={1} session={2} exit=0x{3:X8} elapsed={4}ms",
                tag, pi.dwProcessId, childSession, code, sw.ElapsedMilliseconds);
            CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
        }
        if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
        return result;
    }

    static void Enable(StringBuilder log, string name)
    {
        IntPtr tok;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tok)) return;
        try
        {
            LUID luid;
            if (!LookupPrivilegeValue(null, name, out luid)) return;
            var tp = new TOKEN_PRIVILEGES();
            tp.PrivilegeCount = 1;
            tp.Privileges = new LUID_AND_ATTRIBUTES();
            tp.Privileges.Luid = luid;
            tp.Privileges.Attributes = SE_PRIVILEGE_ENABLED;
            log.AppendLine(string.Format("[priv] {0} -> {1} err={2}", name, AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero), Marshal.GetLastWin32Error()));
        }
        finally { CloseHandle(tok); }
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr e, uint a, IntPtr at, int l, int ty, out IntPtr c);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr t, int c, ref int i, int l);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool LookupPrivilegeValue(string s, string n, out LUID l);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TOKEN_PRIVILEGES n, int l, IntPtr p, IntPtr pl);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr t, string a, string c, IntPtr pa, IntPtr ta, bool i, uint f, IntPtr e, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr e, IntPtr t, bool i);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(IntPtr e);
}
'@

$text = [WinstaSpawn4]::Run($TargetSession, $OutDirectory, $ContainerWsm)
[System.IO.File]::WriteAllText((Join-Path $OutDirectory 'winsta-spawn4.out'), $text)
