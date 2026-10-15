# winsta-spawn5.ps1 - runs as SYSTEM. Does the 0xC0000142 come from the *silo* mismatch
# (our own token in the container's session) or from the *desktop* string? Run the same
# variants with a token taken from a live container process.
param(
    [int] $TargetSession = 4,
    [string] $OutDirectory = 'C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd'
)

$ErrorActionPreference = 'Continue'
Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class WinstaSpawn5
{
    const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    const uint TOKEN_QUERY = 0x8;
    const uint TOKEN_ALL_ACCESS = 0xF01FF;
    const uint MAXIMUM_ALLOWED = 0x02000000;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int  TokenSessionId = 12;
    const int  SecurityImpersonation = 2;
    const int  TokenPrimary = 1;
    const uint SE_PRIVILEGE_ENABLED = 0x2;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    const uint CREATE_NO_WINDOW = 0x08000000;

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

    public static string Run(int session, string outDir)
    {
        var log = new StringBuilder();
        IntPtr own = IntPtr.Zero, mine = IntPtr.Zero;
        Enable(log, "SeTcbPrivilege"); Enable(log, "SeAssignPrimaryTokenPrivilege");
        Enable(log, "SeIncreaseQuotaPrivilege"); Enable(log, "SeDebugPrivilege");
        OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out own);
        DuplicateTokenEx(own, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out mine);
        int sid = session;
        SetTokenInformation(mine, TokenSessionId, ref sid, 4);
        log.AppendLine("[t] our token moved to session " + session);

        // a token from a live container process
        IntPtr cTok = IntPtr.Zero;
        int cpid = 0; string cname = "";
        string[] preferred = new string[] { "regedit", "notepad", "cmd", "powershell", "lsass", "services", "svchost" };
        foreach (var want in preferred)
        {
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.SessionId == session && string.Equals(p.ProcessName, want, StringComparison.OrdinalIgnoreCase)) { cpid = p.Id; cname = p.ProcessName; } }
                catch (Exception) { }
                finally { p.Dispose(); }
                if (cpid != 0) break;
            }
            if (cpid != 0) break;
        }
        log.AppendLine(string.Format("[t] container token source = {0} (pid {1})", cname, cpid));
        if (cpid != 0)
        {
            IntPtr proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, cpid);
            IntPtr raw = IntPtr.Zero;
            if (proc != IntPtr.Zero && OpenProcessToken(proc, 0x0002 | TOKEN_QUERY, out raw))
            {
                IntPtr dupTok = IntPtr.Zero;
                DuplicateTokenEx(raw, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dupTok);
                int tsid = -1, need; IntPtr buf = Marshal.AllocHGlobal(4);
                try { if (GetTokenInformation(dupTok, TokenSessionId, buf, 4, out need)) tsid = Marshal.ReadInt32(buf); }
                finally { Marshal.FreeHGlobal(buf); }
                log.AppendLine("[t] container token session = " + tsid);
                cTok = dupTok;
            }
            else log.AppendLine("[t] OpenProcess/Token failed err=" + Marshal.GetLastWin32Error());
        }

        string cmdExe = @"C:\Windows\System32\cmd.exe";
        string ps = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
        string child = "\"" + ps + "\" -NoProfile -ExecutionPolicy Bypass -File \"C:\\wsm\\winsta-child.ps1\" -OutFile \"C:\\wsm\\winsta-child-v5.out\" -PngDirectory \"C:\\wsm\"";

        if (cTok != IntPtr.Zero)
        {
            log.AppendLine(RunOne("A-container-token-winsta0-exit7", cTok, "\"" + cmdExe + "\" /c exit 7", @"winsta0\default"));
            log.AppendLine(RunOne("B-container-token-null-exit7", cTok, "\"" + cmdExe + "\" /c exit 7", null));
            log.AppendLine(RunOne("C-container-token-service3e4-exit7", cTok, "\"" + cmdExe + "\" /c exit 7", @"Service-0x0-3e4$\Default"));
            log.AppendLine(RunOne("D-container-token-child-winsta0", cTok, child, @"winsta0\default"));
            log.AppendLine(RunOne("E-container-token-child-null", cTok, child, null));
        }
        else
        {
            log.AppendLine("[!] no container token available");
        }

        return log.ToString();
    }

    static string RunOne(string tag, IntPtr token, string cmd, string desktop)
    {
        IntPtr env = IntPtr.Zero;
        CreateEnvironmentBlock(out env, token, false);
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        si.lpDesktop = desktop;
        PROCESS_INFORMATION pi;
        var sw = Stopwatch.StartNew();
        bool ok = CreateProcessAsUser(token, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
            CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, @"C:\Windows", ref si, out pi);
        int err = Marshal.GetLastWin32Error();
        string result;
        if (!ok) result = string.Format("[{0}] CREATE FAILED err={1}", tag, err);
        else
        {
            uint cs = 0; ProcessIdToSessionId((uint)pi.dwProcessId, out cs);
            WaitForSingleObject(pi.hProcess, 8000);
            uint code = 0xDEADBEEF; GetExitCodeProcess(pi.hProcess, out code);
            sw.Stop();
            result = string.Format("[{0}] pid={1} session={2} exit=0x{3:X8} elapsed={4}ms", tag, pi.dwProcessId, cs, code, sw.ElapsedMilliseconds);
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
            AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { CloseHandle(tok); }
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr e, uint a, IntPtr at, int l, int ty, out IntPtr c);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr t, int c, ref int i, int l);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr t, int c, IntPtr i, int l, out int need);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool LookupPrivilegeValue(string s, string n, out LUID l);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TOKEN_PRIVILEGES n, int l, IntPtr p, IntPtr pl);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr t, string a, string c, IntPtr pa, IntPtr ta, bool i, uint f, IntPtr e, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr e, IntPtr t, bool i);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(IntPtr e);
}
'@

$text = [WinstaSpawn5]::Run($TargetSession, $OutDirectory)
[System.IO.File]::WriteAllText((Join-Path $OutDirectory 'winsta-spawn5.out'), $text)
