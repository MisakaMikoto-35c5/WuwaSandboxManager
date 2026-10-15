# winsta-spawn.ps1 - runs as SYSTEM. Puts a process INTO the container's session
# (the "service operates a GUI session" route) instead of trying to cross session
# boundaries with window-station handles.
#
# V1: duplicate our own SYSTEM token, SetTokenInformation(TokenSessionId = N) and
#     CreateProcessAsUser -> a SYSTEM process living in session N (host filesystem).
# V2: duplicate a token from a live container process and CreateProcessAsUser ->
#     a process in the container's session AND silo (a stand-in for HCS process
#     creation, which is broken on this machine right now).
param(
    [int] $TargetSession = 4,
    [string] $ContainerScriptPath = 'C:\wsm\winsta-child.ps1',
    [string] $OutDirectory = 'C:\Users\User\AppData\Local\Temp\opencode\wsmdiag\dd'
)

$ErrorActionPreference = 'Continue'
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class WinstaSpawn
{
    const uint TOKEN_ALL_ACCESS = 0xF01FF;
    const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    const uint TOKEN_QUERY = 0x8;
    const uint MAXIMUM_ALLOWED = 0x02000000;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int  TokenSessionId = 12;
    const int  SecurityImpersonation = 2;
    const int  TokenPrimary = 1;
    const uint SE_PRIVILEGE_ENABLED = 0x2;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x400;
    const uint CREATE_NO_WINDOW = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)]
    struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)]
    struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    public static string Run(int session, string containerScript, string outDir)
    {
        var log = new StringBuilder();
        try
        {
            var me = Process.GetCurrentProcess();
            log.AppendLine(string.Format("[spawn] own pid={0} session={1} user={2}", me.Id, me.SessionId, Environment.UserName));
            Enable(log, "SeTcbPrivilege");
            Enable(log, "SeAssignPrimaryTokenPrivilege");
            Enable(log, "SeIncreaseQuotaPrivilege");
            Enable(log, "SeDebugPrivilege");

            string v1Out = System.IO.Path.Combine(outDir, "winsta-child-v1.out");
            if (System.IO.File.Exists(v1Out)) System.IO.File.Delete(v1Out);

            // ---- V1: our own token, moved to the target session -------------------
            log.AppendLine("[v1] duplicate our token, set TokenSessionId=" + session);
            IntPtr own = IntPtr.Zero, dup = IntPtr.Zero;
            OpenProcessToken(GetCurrentProcess(), TOKEN_ALL_ACCESS, out own);
            bool dupOk = DuplicateTokenEx(own, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out dup);
            log.AppendLine("[v1] DuplicateTokenEx -> " + dupOk + " err=" + Marshal.GetLastWin32Error());
            int sid = session;
            bool setOk = SetTokenInformation(dup, TokenSessionId, ref sid, 4);
            log.AppendLine("[v1] SetTokenInformation(TokenSessionId) -> " + setOk + " err=" + Marshal.GetLastWin32Error());
            if (setOk)
            {
                string cmd = Quote(PowerShell()) + " -NoProfile -ExecutionPolicy Bypass -File \"" +
                    System.IO.Path.Combine(outDir, "winsta-child.ps1") + "\" -OutFile \"" + v1Out +
                    "\" -PngDirectory \"" + outDir + "\"";
                log.Append(Spawn("v1", dup, cmd, log));
            }

            // ---- V2: a token from a live container process ------------------------
            string v2Out = System.IO.Path.Combine(outDir, "winsta-child-v2.out");
            if (System.IO.File.Exists(v2Out)) System.IO.File.Delete(v2Out);

            int containerPid = 0;
            string containerName = "";
            string[] preferred = new string[] { "regedit", "notepad", "explorer", "cmd", "powershell" };
            foreach (var want in preferred)
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.SessionId == session && string.Equals(p.ProcessName, want, StringComparison.OrdinalIgnoreCase))
                        {
                            containerPid = p.Id; containerName = p.ProcessName; break;
                        }
                    }
                    catch (Exception) { }
                    finally { p.Dispose(); }
                }
                if (containerPid != 0) break;
            }
            log.AppendLine(string.Format("[v2] container process = {0} (pid {1})", containerName, containerPid));

            if (containerPid != 0)
            {
                IntPtr proc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, containerPid);
                log.AppendLine("[v2] OpenProcess -> 0x" + proc.ToInt64().ToString("X") + " err=" + Marshal.GetLastWin32Error());
                if (proc != IntPtr.Zero)
                {
                    IntPtr cTok = IntPtr.Zero, cDup = IntPtr.Zero;
                    bool ot = OpenProcessToken(proc, 0x0002 | TOKEN_QUERY, out cTok);
                    log.AppendLine("[v2] OpenProcessToken -> " + ot + " err=" + Marshal.GetLastWin32Error());
                    if (ot)
                    {
                        bool d2 = DuplicateTokenEx(cTok, MAXIMUM_ALLOWED, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out cDup);
                        log.AppendLine("[v2] DuplicateTokenEx -> " + d2 + " err=" + Marshal.GetLastWin32Error());
                        if (d2)
                        {
                            int tsid = -1;
                            int need;
                            IntPtr buf = Marshal.AllocHGlobal(4);
                            try
                            {
                                if (GetTokenInformation(cDup, TokenSessionId, buf, 4, out need)) tsid = Marshal.ReadInt32(buf);
                            }
                            finally { Marshal.FreeHGlobal(buf); }
                            log.AppendLine("[v2] container token session = " + tsid);

                            string cmd2 = Quote(PowerShell()) + " -NoProfile -ExecutionPolicy Bypass -File \"" +
                                containerScript + "\" -OutFile \"C:\\wsm\\winsta-child-v2.out\" -PngDirectory \"C:\\wsm\"";
                            log.Append(Spawn("v2", cDup, cmd2, log));
                        }
                    }
                }
            }
        }
        catch (Exception ex) { log.AppendLine("[spawn] EX " + ex); }
        return log.ToString();
    }

    static string Spawn(string tag, IntPtr token, string commandLine, StringBuilder log)
    {
        var sb = new StringBuilder();
        IntPtr env = IntPtr.Zero;
        CreateEnvironmentBlock(out env, token, false);
        string[] desktops = new string[] { @"winsta0\default", @"Service-0x0-3e7$\Default", @"Service-0x0-3e4$\Default", null };
        foreach (var d in desktops)
        {
            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf(typeof(STARTUPINFO));
            si.lpDesktop = d;
            PROCESS_INFORMATION pi;
            bool ok = CreateProcessAsUser(token, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW, env, null, ref si, out pi);
            int err = Marshal.GetLastWin32Error();
            string ds = d == null ? "(null)" : d;
            if (ok)
            {
                uint childSession;
                ProcessIdToSessionId((uint)pi.dwProcessId, out childSession);
                sb.AppendLine(string.Format("[{0}] spawned on desktop {1}: pid={2} session={3}", tag, ds, pi.dwProcessId, childSession));
                CloseHandle(pi.hProcess);
                CloseHandle(pi.hThread);
                break;
            }
            sb.AppendLine(string.Format("[{0}] desktop {1} FAILED err={2}", tag, ds, err));
        }
        if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
        return sb.ToString();
    }

    static void Enable(StringBuilder log, string name)
    {
        IntPtr tok;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tok)) return;
        try
        {
            LUID luid;
            if (!LookupPrivilegeValue(null, name, out luid)) { log.AppendLine("[priv] lookup " + name + " failed"); return; }
            var tp = new TOKEN_PRIVILEGES();
            tp.PrivilegeCount = 1;
            tp.Privileges = new LUID_AND_ATTRIBUTES();
            tp.Privileges.Luid = luid;
            tp.Privileges.Attributes = SE_PRIVILEGE_ENABLED;
            bool ok = AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            log.AppendLine(string.Format("[priv] {0} -> {1} err={2}", name, ok, Marshal.GetLastWin32Error()));
        }
        finally { CloseHandle(tok); }
    }

    static string Quote(string s) { return "\"" + s + "\""; }
    static string PowerShell()
    {
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe");
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr existing, uint access, IntPtr attrs, int level, int type, out IntPtr created);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetTokenInformation(IntPtr token, int infoClass, ref int info, int length);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, int len, IntPtr prev, IntPtr prevLen);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr token, string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)] static extern bool DestroyEnvironmentBlock(IntPtr env);
}
'@

$outFile = Join-Path $OutDirectory 'winsta-spawn.out'
$text = [WinstaSpawn]::Run($TargetSession, $ContainerScriptPath, $OutDirectory)
[System.IO.File]::WriteAllText($outFile, $text)
