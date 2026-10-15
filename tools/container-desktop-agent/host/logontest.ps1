# Runs AS SYSTEM: is the image Administrator's password blank? (If it is, an
# offline Winlogon\DefaultPassword="" makes winlogon's auto-logon fire at boot.)
$ErrorActionPreference = 'Continue'

$src = @'
using System;
using System.Runtime.InteropServices;

public static class LogonProbe
{
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LogonUser(string user, string domain, string password, int logonType, int provider, out IntPtr token);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public static string Try(string user, string password)
    {
        IntPtr token;
        bool ok = LogonUser(user, ".", password, 2, 0, out token);
        int err = Marshal.GetLastWin32Error();
        if (ok) { CloseHandle(token); return "LogonUser(" + user + ", '" + password + "') => OK"; }
        return "LogonUser(" + user + ", '" + password + "') => err " + err + " (" + new System.ComponentModel.Win32Exception(err).Message + ")";
    }
}
'@

Add-Type -TypeDefinition $src -Language CSharp
Write-Output ('identity: ' + (whoami))
[LogonProbe]::Try('Administrator', '')
[LogonProbe]::Try('Administrator', 'Wsm!Passw0rd')
[LogonProbe]::Try('Administrator', 'Password1')
[LogonProbe]::Try('WDAGUtilityAccount', '')
[LogonProbe]::Try('Guest', '')
