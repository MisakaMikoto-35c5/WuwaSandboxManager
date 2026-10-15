# sessions.ps1 - report the session / window station / desktop this process sees.
# Run it BOTH inside the container and on the host: the point is that the two
# disagree about "the console session", which is exactly why a silo's windows are
# not the host console's windows.
$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class S {
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ProcessIdToSessionId(uint pid, out uint sid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentProcessId();
    [DllImport("user32.dll")] public static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint tid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder info, int len, out int needed);
    [DllImport("wtsapi32.dll", SetLastError = true)] public static extern bool WTSQuerySessionInformationW(IntPtr server, uint session, int infoClass, out IntPtr buffer, out uint bytes);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr h);

    public static string NameOf(IntPtr h) {
        if (h == IntPtr.Zero) return "(null)";
        StringBuilder sb = new StringBuilder(256);
        int n;
        return GetUserObjectInformationW(h, 2, sb, 512, out n) ? sb.ToString() : "(err " + Marshal.GetLastWin32Error() + ")";
    }
    public static string SessionInfo(uint session) {
        IntPtr buf; uint len;
        // 8 = WTSWinStationName, 11 = WTSUserName, 16 = WTSDomainName
        string ws = "", user = "", dom = "";
        if (WTSQuerySessionInformationW(IntPtr.Zero, session, 8, out buf, out len)) { ws = Marshal.PtrToStringUni(buf); LocalFree(buf); }
        if (WTSQuerySessionInformationW(IntPtr.Zero, session, 11, out buf, out len)) { user = Marshal.PtrToStringUni(buf); LocalFree(buf); }
        if (WTSQuerySessionInformationW(IntPtr.Zero, session, 16, out buf, out len)) { dom = Marshal.PtrToStringUni(buf); LocalFree(buf); }
        return "winsta='" + ws + "' user='" + dom + "\\" + user + "'";
    }
}
'@

$sid = 0
[void][S]::ProcessIdToSessionId([S]::GetCurrentProcessId(), [ref]$sid)
$console = [S]::WTSGetActiveConsoleSessionId()
Write-Output ("own  pid={0} session={1}" -f [S]::GetCurrentProcessId(), $sid)
Write-Output ("WTSGetActiveConsoleSessionId = {0}" -f $console)
Write-Output ("process window station = '{0}'  thread desktop = '{1}'" -f [S]::NameOf([S]::GetProcessWindowStation()), [S]::NameOf([S]::GetThreadDesktop([S]::GetCurrentThreadId())))
Write-Output ("session {0} info: {1}" -f $console, [S]::SessionInfo($console))
Write-Output 'sessions the WTS API can see from here:'
for ($i = 0; $i -le 8; $i++) {
    $info = [S]::SessionInfo($i)
    if ($info -notmatch "user='\\\\'") { Write-Output ("  [{0}] {1}" -f $i, $info) }
}
