<#
.SYNOPSIS
  Runs a command line in a chosen session / desktop of the machine the script
  runs on, by borrowing the token of a process that already lives in that
  session (the "psexec -i" trick), instead of going through RDP or a logon.

.DESCRIPTION
  A process-isolated Windows container gets its interactive <c>console</c>
  session (winlogon + LogonUI) as soon as its image boots with
  Session Manager\NumberOfInitialSessions = 2. Nothing can reach that session
  from the outside, though: RDP refuses to create a listener inside a container
  (termsrv's WTSIsServerContainer path) and the container's own main process
  runs in the services session. Auto-logon cannot help either, because the
  container's user (ContainerAdministrator) is not a SAM account - `net user`
  lists only Administrator/DefaultAccount/Guest/WDAGUtilityAccount.

  This script is the way in: it runs *inside* the container (in the services
  session), finds a process that is already in the target session - by default
  winlogon.exe / LogonUI.exe in the console session - duplicates that process's
  token and starts the command on the requested desktop:

    winsta0\default    the normal interactive desktop (a shell would use this)
    winsta0\Winlogon   the secure desktop where LogonUI draws the logon screen

  Privilege notes (measured in a Server Core container): SeDebugPrivilege and
  SeImpersonatePrivilege are present and enabled, but
  SeAssignPrimaryTokenPrivilege is NOT in the token at all - so a *primary*
  token cannot be used. The script therefore tries, in order:

    1. impersonation token + CreateProcessAsUser   (works without the privilege)
    2. primary token       + CreateProcessAsUser
    3. impersonation token + CreateProcessWithTokenW
    4. primary token       + CreateProcessWithTokenW

.PARAMETER Session
  Target session id. In a prepared container the console session is the only
  non-zero session; the script finds it itself with -AutoSession (the default).

.PARAMETER Desktop
  Desktop to create the process on. Default winsta0\default.

.PARAMETER CommandLine
  The command line to run in the target session.

.PARAMETER Visible
  Give the child its own console window on that desktop (CREATE_NEW_CONSOLE)
  instead of running it hidden. Handy to prove the desktop really draws.

.EXAMPLE
  powershell -NoProfile -File C:\wsm\session-run.ps1 -AutoSession -CommandLine 'regedt32.exe' -Visible
#>
[CmdletBinding()]
param(
    [int] $Session = -1,

    [switch] $AutoSession,

    [string] $Desktop = 'winsta0\default',

    [string] $CommandLine,

    [switch] $Visible,

    # Reads Session/Desktop/CommandLine/Visible from a "key=value" file. A
    # scheduled task that runs as SYSTEM (see session-run-as-system.ps1) uses this
    # so its action stays quote-free.
    [string] $ArgumentFile
)

$ErrorActionPreference = 'Stop'

if ($ArgumentFile) {
    foreach ($line in (Get-Content -LiteralPath $ArgumentFile)) {
        if ($line -match '^\s*([A-Za-z]+)\s*=\s*(.*)$') {
            switch ($matches[1].ToLowerInvariant()) {
                'desktop' { $Desktop = $matches[2].Trim() }
                'commandline' { $CommandLine = $matches[2].Trim() }
                'visible' { $Visible = [bool][int]$matches[2].Trim() }
                'session' { $Session = [int]$matches[2].Trim() }
                'autosession' { $AutoSession = [bool][int]$matches[2].Trim() }
            }
        }
    }
}

if (-not $CommandLine) {
    throw 'No command line: pass -CommandLine or an -ArgumentFile containing CommandLine=... .'
}

# C# 5 only: the in-box csc.exe that Add-Type drives in a Server Core container
# is the .NET Framework 4.x compiler (no interpolation, no expression bodies).
$source = @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class SessionLauncher
{
    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int TOKEN_ASSIGN_PRIMARY = 0x0001;
    private const int TOKEN_DUPLICATE = 0x0002;
    private const int TOKEN_QUERY = 0x0008;
    private const int TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const int TOKEN_ADJUST_DEFAULT = 0x0080;
    private const int TOKEN_ADJUST_SESSIONID = 0x0100;
    private const int MAXIMUM_ALLOWED = 0x02000000;
    private const int SECURITY_IMPERSONATION = 2;
    private const int TOKEN_PRIMARY = 1;
    private const int TOKEN_IMPERSONATION = 2;
    private const int SE_PRIVILEGE_ENABLED = 0x00000002;
    private const int CREATE_NEW_CONSOLE = 0x00000010;
    private const int CREATE_NO_WINDOW = 0x08000000;
    private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public int Low; public int High; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public int Count; public LUID Luid; public int Attributes; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr token, int access, IntPtr attributes,
        int level, int type, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr token, string application, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, int flags,
        IntPtr environment, string currentDirectory, ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessWithTokenW(IntPtr token, int logonFlags, string application,
        StringBuilder commandLine, int flags, IntPtr environment, string currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    private static void Enable(string name)
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
        {
            return;
        }

        LUID luid;
        if (LookupPrivilegeValue(null, name, out luid))
        {
            TOKEN_PRIVILEGES state = new TOKEN_PRIVILEGES();
            state.Count = 1;
            state.Luid = luid;
            state.Attributes = SE_PRIVILEGE_ENABLED;
            AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
        }

        CloseHandle(token);
    }

    public static string SessionMap()
    {
        StringBuilder text = new StringBuilder();
        Process[] all = Process.GetProcesses();
        for (int i = 0; i < all.Length; i++)
        {
            try
            {
                text.Append(all[i].ProcessName).Append("#").Append(all[i].Id)
                    .Append("@").Append(all[i].SessionId).Append(" ");
            }
            catch { }
        }

        return text.ToString();
    }

    private static Process FindInSession(int session)
    {
        // winlogon / LogonUI are what makes a session "interactive", so prefer
        // them; otherwise take anything that lives in the session.
        string[] preferred = new string[] { "winlogon", "LogonUI", "csrss", "fontdrvhost", "dwm", "rdpclip" };
        for (int i = 0; i < preferred.Length; i++)
        {
            Process[] found = Process.GetProcessesByName(preferred[i]);
            for (int j = 0; j < found.Length; j++)
            {
                try { if (found[j].SessionId == session) { return found[j]; } }
                catch { }
            }
        }

        Process[] all = Process.GetProcesses();
        for (int i = 0; i < all.Length; i++)
        {
            try { if (all[i].SessionId == session) { return all[i]; } }
            catch { }
        }

        return null;
    }

    private static string Try(IntPtr token, int tokenType, bool withToken, string desktop,
        string commandLine, int flags, out int pid)
    {
        pid = 0;

        IntPtr duplicate;
        if (!DuplicateTokenEx(token, MAXIMUM_ALLOWED, IntPtr.Zero, SECURITY_IMPERSONATION, tokenType, out duplicate))
        {
            return "DuplicateTokenEx(type=" + tokenType + ")=" + Marshal.GetLastWin32Error();
        }

        STARTUPINFO startup = new STARTUPINFO();
        startup.cb = Marshal.SizeOf(typeof(STARTUPINFO));
        startup.lpDesktop = desktop;

        PROCESS_INFORMATION info;
        StringBuilder mutable = new StringBuilder(commandLine);
        bool ok;
        if (withToken)
        {
            ok = CreateProcessWithTokenW(duplicate, 0, null, mutable, flags, IntPtr.Zero, null, ref startup, out info);
        }
        else
        {
            ok = CreateProcessAsUser(duplicate, null, mutable, IntPtr.Zero, IntPtr.Zero, false, flags,
                IntPtr.Zero, null, ref startup, out info);
        }

        int error = Marshal.GetLastWin32Error();
        CloseHandle(duplicate);

        if (!ok)
        {
            string api = withToken ? "CreateProcessWithTokenW" : "CreateProcessAsUser";
            return api + "(type=" + tokenType + ")=" + error + " ("
                + new System.ComponentModel.Win32Exception(error).Message + ")";
        }

        pid = info.dwProcessId;
        CloseHandle(info.hProcess);
        CloseHandle(info.hThread);
        return null;
    }

    public static string Run(int session, string desktop, string commandLine, bool visible)
    {
        Enable("SeDebugPrivilege");
        Enable("SeIncreaseQuotaPrivilege");
        Enable("SeAssignPrimaryTokenPrivilege");

        Process source = FindInSession(session);
        if (source == null)
        {
            return "FAIL no process found in session " + session + " (seen: " + SessionMap() + ")";
        }

        string origin = source.ProcessName + "#" + source.Id + "@" + source.SessionId;

        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, source.Id);
        if (process == IntPtr.Zero)
        {
            return "FAIL OpenProcess(" + origin + ")=" + Marshal.GetLastWin32Error();
        }

        IntPtr token;
        // Note: ask only for TOKEN_DUPLICATE|TOKEN_QUERY. Adding
        // TOKEN_ASSIGN_PRIMARY/TOKEN_ADJUST_DEFAULT/TOKEN_ADJUST_SESSIONID (0x10B)
        // makes OpenProcessToken fail with ACCESS_DENIED even though
        // SeDebugPrivilege is enabled, because a container administrator's token
        // simply does not hold those rights.
        if (!OpenProcessToken(process, TOKEN_DUPLICATE | TOKEN_QUERY, out token))
        {
            int e = Marshal.GetLastWin32Error();
            CloseHandle(process);
            return "FAIL OpenProcessToken(" + origin + ")=" + e + " ("
                + new System.ComponentModel.Win32Exception(e).Message + ")";
        }

        int flags = CREATE_UNICODE_ENVIRONMENT | (visible ? CREATE_NEW_CONSOLE : CREATE_NO_WINDOW);
        int pid = 0;
        string method = null;
        string error = null;
        StringBuilder attemptsLog = new StringBuilder();

        // Order matters. A container administrator has SeImpersonatePrivilege but
        // NOT SeAssignPrimaryTokenPrivilege, so CreateProcessAsUser fails and only
        // CreateProcessWithTokenW works - and that one creates the child in the
        // CALLER's session, not the token's. Running this script as SYSTEM (via a
        // scheduled task, see session-run-as-system.ps1) makes the first path work.
        string[,] attempts = new string[,]
        {
            { "impersonation token + CreateProcessAsUser", "0", "0" },
            { "impersonation token + CreateProcessWithTokenW", "1", "1" },
            { "primary token + CreateProcessAsUser", "0", "2" },
            { "primary token + CreateProcessWithTokenW", "1", "2" },
        };

        for (int i = 0; i < attempts.GetLength(0) && method == null; i++)
        {
            bool withToken = attempts[i, 1] == "1";
            int type = attempts[i, 2] == "2" ? TOKEN_PRIMARY : TOKEN_IMPERSONATION;
            error = Try(token, type, withToken, desktop, commandLine, flags, out pid);
            if (error == null)
            {
                method = attempts[i, 0];
            }
            else
            {
                attemptsLog.Append(attempts[i, 0]).Append(" => ").Append(error).Append("; ");
            }
        }

        CloseHandle(token);
        CloseHandle(process);

        if (method == null)
        {
            return "FAIL every creation path [ " + attemptsLog.ToString() + "]";
        }

        string sessionOfChild = "?";
        try
        {
            sessionOfChild = Convert.ToString(Process.GetProcessById(pid).SessionId);
        }
        catch { }

        return "OK pid=" + pid + " childSession=" + sessionOfChild + " targetSession=" + session
            + " desktop=" + desktop + " via=" + method + " borrowed=" + origin + " cmd=" + commandLine
            + " [ " + attemptsLog.ToString() + "]";
    }
}
'@

# Compiling that C# takes 20-40 s under the in-box csc in a Server Core container
# and every launch is a fresh process, so cache the compiled assembly. It is
# rebuilt automatically when this script is newer than the dll.
$assemblyFile = Join-Path (Split-Path -Parent $PSCommandPath) 'SessionLauncher.dll'
if ((Test-Path $assemblyFile) -and
    ((Get-Item $PSCommandPath).LastWriteTimeUtc -gt (Get-Item $assemblyFile).LastWriteTimeUtc)) {
    Remove-Item $assemblyFile -Force -ErrorAction SilentlyContinue
}

$loaded = $false
if (Test-Path $assemblyFile) {
    try {
        Add-Type -Path $assemblyFile -ErrorAction Stop
        $loaded = $true
    }
    catch { $loaded = $false }
}

if (-not $loaded) {
    try {
        Add-Type -TypeDefinition $source -Language CSharp -OutputAssembly $assemblyFile -ErrorAction Stop
        Add-Type -Path $assemblyFile -ErrorAction Stop
    }
    catch {
        Add-Type -TypeDefinition $source -Language CSharp
    }
}

if ($AutoSession -or $Session -lt 0) {
    # The console session is the interactive one; winlogon only runs there.
    $winlogon = Get-Process -Name winlogon -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -ne 0 } | Select-Object -First 1
    if (-not $winlogon) {
        Write-Output 'FAIL no winlogon outside the services session - does the image set NumberOfInitialSessions=2?'
        Write-Output ('sessions: ' + [SessionLauncher]::SessionMap())
        exit 1
    }

    $Session = $winlogon.SessionId
}

Write-Output ("target session: {0}" -f $Session)
Write-Output ([SessionLauncher]::Run($Session, $Desktop, $CommandLine, [bool]$Visible))
