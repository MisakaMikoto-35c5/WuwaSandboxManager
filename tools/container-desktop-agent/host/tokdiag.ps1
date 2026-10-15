# Why can't ContainerAdministrator borrow a session-5 token? And could a logon work?
$ErrorActionPreference = 'Continue'

Write-Output '==== whoami ===='
whoami
whoami /user
(whoami /priv 2>&1 | Select-String 'Debug|Assign|Impersonate|Increase|Tcb') -join "`n"

$src = @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class TokenProbe
{
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, int access, out IntPtr t);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr t, bool all, ref TP s, int len, IntPtr prev, IntPtr ret);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool LookupPrivilegeValue(string sys, string name, out LUID luid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(int access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)] struct LUID { public int Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct TP { public int Count; public LUID Luid; public int Attr; }

    static string TryEnable(string name)
    {
        IntPtr t;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out t))
        {
            return name + ": OpenProcessToken(self) err " + Marshal.GetLastWin32Error();
        }
        LUID luid;
        if (!LookupPrivilegeValue(null, name, out luid))
        {
            int e = Marshal.GetLastWin32Error();
            CloseHandle(t);
            return name + ": LookupPrivilegeValue err " + e;
        }
        TP s = new TP();
        s.Count = 1;
        s.Luid = luid;
        s.Attr = 2;
        bool ok = AdjustTokenPrivileges(t, false, ref s, 0, IntPtr.Zero, IntPtr.Zero);
        int err = Marshal.GetLastWin32Error();
        CloseHandle(t);
        return name + ": Adjust=" + ok + " err=" + err + (err == 1300 ? " (ERROR_NOT_ALL_ASSIGNED)" : "");
    }

    public static string Report()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("-- privileges --");
        sb.AppendLine(TryEnable("SeDebugPrivilege"));
        sb.AppendLine(TryEnable("SeImpersonatePrivilege"));
        sb.AppendLine(TryEnable("SeAssignPrimaryTokenPrivilege"));
        sb.AppendLine(TryEnable("SeIncreaseQuotaPrivilege"));
        sb.AppendLine(TryEnable("SeTcbPrivilege"));

        int[] masks = new int[] { 0x1000, 0x0400, 0x1F0FFF, 0x02000000 };
        string[] maskNames = new string[] { "QUERY_LIMITED(0x1000)", "QUERY_INFORMATION(0x400)", "ALL(0x1F0FFF)", "MAXIMUM_ALLOWED" };
        int[] tokenMasks = new int[] { 0x0008, 0x000A, 0x010B, 0x02000000 };
        string[] tokenNames = new string[] { "TOKEN_QUERY(0x8)", "DUP|QUERY(0xA)", "DUP|QUERY|ASSIGN|ADJDEFAULT|ADJSID(0x10B)", "MAXIMUM_ALLOWED" };

        sb.AppendLine("-- token access matrix (non-zero sessions) --");
        Process[] all = Process.GetProcesses();
        for (int i = 0; i < all.Length; i++)
        {
            int sid;
            try { sid = all[i].SessionId; }
            catch { continue; }
            if (sid == 0) { continue; }
            string name = all[i].ProcessName + "#" + all[i].Id + "@" + sid;
            for (int m = 0; m < masks.Length; m++)
            {
                IntPtr h = OpenProcess(masks[m], false, all[i].Id);
                if (h == IntPtr.Zero)
                {
                    sb.AppendLine(name + " " + maskNames[m] + " -> OpenProcess err " + Marshal.GetLastWin32Error());
                    continue;
                }
                for (int k = 0; k < tokenMasks.Length; k++)
                {
                    IntPtr token;
                    bool ok = OpenProcessToken(h, tokenMasks[k], out token);
                    if (ok) { CloseHandle(token); }
                    else
                    {
                        int err = Marshal.GetLastWin32Error();
                        sb.AppendLine(name + " " + maskNames[m] + " -> token " + tokenNames[k] + " err " + err
                            + " (" + new System.ComponentModel.Win32Exception(err).Message + ")");
                    }
                }
                CloseHandle(h);
            }
        }
        return sb.ToString();
    }
}
'@

Add-Type -TypeDefinition $src -Language CSharp
Write-Output '==== token probe ===='
[TokenProbe]::Report()

Write-Output '==== could a SAM logon work? ===='
(net user Administrator 2>&1 | Out-String)
(net user WDAGUtilityAccount 2>&1 | Out-String)
(sc.exe query Schedule 2>&1 | Out-String)
(sc.exe qc DcomLaunch 2>&1 | Select-String 'SERVICE_START_NAME|BINARY' | Out-String)
