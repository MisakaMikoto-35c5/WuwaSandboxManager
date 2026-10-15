# Cross-session process creation probe, designed to run AS SYSTEM (via a task).
$ErrorActionPreference = 'Continue'

$src = @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class CrossSession
{
    [StructLayout(LayoutKind.Sequential)] struct LUID { public int Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TP { public int Count; public LUID Luid; public int Attr; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SI
    {
        public int cb; public string r; public string desktop; public string title;
        public int x, y, xs, ys, xc, yc, fill, flags;
        public short show, r2; public IntPtr p2, hIn, hOut, hErr;
    }
    [StructLayout(LayoutKind.Sequential)] struct PI { public IntPtr p, t; public int pid, tid; }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int a, bool i, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, int a, out IntPtr t);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(IntPtr t, int a, IntPtr sa, int lvl, int type, out IntPtr nt);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessAsUser(IntPtr t, string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inh, int f, IntPtr env, string cwd, ref SI si, out PI pi);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessWithTokenW(IntPtr t, int lf, string app, StringBuilder cmd, int f, IntPtr env, string cwd, ref SI si, out PI pi);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValue(string s, string n, out LUID l);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr t, bool d, ref TP s, int len, IntPtr p, IntPtr r);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr t, int cls, IntPtr info, int len, out int ret);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr str);

    static string Enable(string name)
    {
        IntPtr t;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out t)) { return name + ": open self token err " + Marshal.GetLastWin32Error(); }
        LUID luid;
        if (!LookupPrivilegeValue(null, name, out luid)) { CloseHandle(t); return name + ": lookup err " + Marshal.GetLastWin32Error(); }
        TP s = new TP(); s.Count = 1; s.Luid = luid; s.Attr = 2;
        bool ok = AdjustTokenPrivileges(t, false, ref s, 0, IntPtr.Zero, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();
        CloseHandle(t);
        return name + ": adjust=" + ok + " lasterr=" + err + (err == 1300 ? " (NOT_ALL_ASSIGNED)" : "");
    }

    static string TokenSession(IntPtr t)
    {
        IntPtr buf = Marshal.AllocHGlobal(16);
        int ret;
        bool ok = GetTokenInformation(t, 12, buf, 16, out ret);   // TokenSessionId
        int sid = ok ? Marshal.ReadInt32(buf) : -1;
        Marshal.FreeHGlobal(buf);
        return ok ? sid.ToString() : "err " + Marshal.GetLastWin32Error();
    }

    static string TokenUser(IntPtr t)
    {
        int need;
        GetTokenInformation(t, 1, IntPtr.Zero, 0, out need);
        IntPtr buf = Marshal.AllocHGlobal(need);
        if (!GetTokenInformation(t, 1, buf, need, out need)) { Marshal.FreeHGlobal(buf); return "err"; }
        IntPtr sidPtr = Marshal.ReadIntPtr(buf);
        IntPtr str;
        string text = ConvertSidToStringSid(sidPtr, out str) ? Marshal.PtrToStringUni(str) : "err";
        if (str != IntPtr.Zero) { Marshal.FreeHGlobal(str); }
        Marshal.FreeHGlobal(buf);
        return text;
    }

    static string Try(IntPtr t, bool withToken, string desktop, string cmd)
    {
        SI si = new SI();
        si.cb = Marshal.SizeOf(typeof(SI));
        si.desktop = desktop;
        PI pi;
        StringBuilder sb = new StringBuilder(cmd);
        bool ok = withToken
            ? CreateProcessWithTokenW(t, 0, null, sb, 0x400, IntPtr.Zero, null, ref si, out pi)
            : CreateProcessAsUser(t, null, sb, IntPtr.Zero, IntPtr.Zero, false, 0x400, IntPtr.Zero, null, ref si, out pi);
        int err = Marshal.GetLastWin32Error();
        if (!ok) { return (withToken ? "WithTokenW" : "AsUser") + " desktop='" + (desktop ?? "<null>") + "' err " + err + " (" + new System.ComponentModel.Win32Exception(err).Message + ")"; }
        int session = -1;
        try { session = Process.GetProcessById(pi.pid).SessionId; } catch { }
        CloseHandle(pi.p); CloseHandle(pi.t);
        return (withToken ? "WithTokenW" : "AsUser") + " desktop='" + (desktop ?? "<null>") + "' OK pid=" + pi.pid + " session=" + session;
    }

    public static string Probe()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("whoami='" + Environment.UserName + "'");
        sb.AppendLine(Enable("SeDebugPrivilege"));
        sb.AppendLine(Enable("SeAssignPrimaryTokenPrivilege"));
        sb.AppendLine(Enable("SeIncreaseQuotaPrivilege"));
        sb.AppendLine(Enable("SeTcbPrivilege"));

        Process source = null;
        foreach (Process p in Process.GetProcessesByName("winlogon"))
        {
            if (p.SessionId != 0) { source = p; break; }
        }
        if (source == null) { return sb.AppendLine("no winlogon outside session 0").ToString(); }
        sb.AppendLine("source=" + source.ProcessName + "#" + source.Id + "@" + source.SessionId);

        IntPtr proc = OpenProcess(0x1000, false, source.Id);
        if (proc == IntPtr.Zero) { return sb.AppendLine("OpenProcess err " + Marshal.GetLastWin32Error()).ToString(); }
        IntPtr token;
        if (!OpenProcessToken(proc, 0x0002 | 0x0008, out token))
        {
            return sb.AppendLine("OpenProcessToken err " + Marshal.GetLastWin32Error()).ToString();
        }

        IntPtr primary, imp;
        if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 1, out primary)) { sb.AppendLine("dup primary err " + Marshal.GetLastWin32Error()); }
        else { sb.AppendLine("primary token: session=" + TokenSession(primary) + " user=" + TokenUser(primary)); }
        if (!DuplicateTokenEx(token, 0x02000000, IntPtr.Zero, 2, 2, out imp)) { sb.AppendLine("dup imp err " + Marshal.GetLastWin32Error()); }
        else { sb.AppendLine("impersonation token: session=" + TokenSession(imp) + " user=" + TokenUser(imp)); }

        string cmd = "C:\\Windows\\System32\\cmd.exe";
        sb.AppendLine(Try(primary, false, null, cmd));
        sb.AppendLine(Try(primary, false, "winsta0\\default", cmd));
        sb.AppendLine(Try(imp, false, null, cmd));
        sb.AppendLine(Try(imp, false, "winsta0\\default", cmd));
        sb.AppendLine(Try(primary, true, "winsta0\\default", cmd));
        return sb.ToString();
    }
}
'@

Add-Type -TypeDefinition $src -Language CSharp
Write-Output '==== cross-session probe ===='
[CrossSession]::Probe()
Write-Output '==== sessions ===='
(Get-Process | Group-Object SessionId | ForEach-Object {
        "session {0}: {1} procs: {2}" -f $_.Name, $_.Count,
        (($_.Group | Select-Object -ExpandProperty ProcessName | Sort-Object -Unique) -join ',')
    }) -join "`n"
