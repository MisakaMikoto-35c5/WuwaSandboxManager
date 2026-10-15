# winsta-spawn3.ps1 - runs as SYSTEM. Definitive test: with our own token moved to
# the container's session, write to a CONTAINER path (C:\wsm) and run the child probe
# from there. If the markers show up through the host-visible mount, we have a
# "process in the container session" mechanism that needs neither HCS nor injection.
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

public static class WinstaSpawn3
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
        IntPtr own = IntPtr.Zero, dup = IntPtr.Zero;
        Enable(log, "SeTcbPrivilege"); Enable(log, "SeAssignPrimaryTokenPrivilege");
        Enable(log, "SeIncreaseQuotaPrivilege"); Enable(log, "SeDebugPrivilege");
        OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out own);
        DuplicateTokenEx(own, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dup);
        int sid = session;
        log.AppendLine("[t] SetTokenInformation(TokenSessionId=" + session + ") -> " + SetTokenInformation(dup, TokenSessionId, ref sid, 4));

        string cmdExe = @"C:\Windows\System32\cmd.exe";
        string ps = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";

        Try(log, "1-cmd-containerpath", dup,
            "\"" + cmdExe + "\" /c echo 1-cmd-containerpath > C:\\wsm\\spawn1.txt",
            @"winsta0\default", "C:\\Windows");

        Try(log, "2-child-probe", dup,
            "\"" + ps + "\" -NoProfile -ExecutionPolicy Bypass -File \"C:\\wsm\\winsta-child.ps1\" -OutFile \"C:\\wsm\\winsta-child-v1.out\" -PngDirectory \"C:\\wsm\"",
            @"winsta0\default", "C:\\Windows");

        Try(log, "3-child-servicewinsta", dup,
            "\"" + ps + "\" -NoProfile -ExecutionPolicy Bypass -File \"C:\\wsm\\winsta-child.ps1\" -OutFile \"C:\\wsm\\winsta-child-v3.out\" -PngDirectory \"C:\\wsm\"",
            @"Service-0x0-3e7$\Default", "C:\\Windows");

        return log.ToString();
    }

    static void Try(StringBuilder log, string tag, IntPtr token, string cmd, string desktop, string cwd)
    {
        IntPtr env = IntPtr.Zero;
        CreateEnvironmentBlock(out env, token, false);
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        si.lpDesktop = desktop;
        PROCESS_INFORMATION pi;
        bool ok = CreateProcessAsUser(token, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
            CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, cwd, ref si, out pi);
        int err = Marshal.GetLastWin32Error();
        if (ok)
        {
            log.AppendLine(string.Format("[{0}] OK pid={1}", tag, pi.dwProcessId));
            CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
        }
        else
        {
            log.AppendLine(string.Format("[{0}] FAILED err={1}", tag, err));
        }
        if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
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

$text = [WinstaSpawn3]::Run($TargetSession, $OutDirectory)
[System.IO.File]::WriteAllText((Join-Path $OutDirectory 'winsta-spawn3.out'), $text)
