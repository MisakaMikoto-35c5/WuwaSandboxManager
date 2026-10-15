# Cross-silo handle test, privileged: can the host open a container process for
# handle duplication (with SeDebugPrivilege) and map the container's section?
$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class P {
    [StructLayout(LayoutKind.Sequential)] public struct LUID_AND_ATTRIBUTES { public long Luid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] public struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privilege; }

    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool DuplicateHandle(IntPtr srcProc, IntPtr srcHandle,
        IntPtr dstProc, out IntPtr dstHandle, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint offHi, uint offLo, ulong length);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] public static extern bool LookupPrivilegeValueW(string system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);

    public static string EnableDebug() {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 | 0x8, out token)) return "OpenProcessToken failed " + Marshal.GetLastWin32Error();
        long luid;
        if (!LookupPrivilegeValueW(null, "SeDebugPrivilege", out luid)) return "LookupPrivilegeValue failed " + Marshal.GetLastWin32Error();
        TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
        tp.PrivilegeCount = 1;
        tp.Privilege.Luid = luid;
        tp.Privilege.Attributes = 0x2; // SE_PRIVILEGE_ENABLED
        int size = Marshal.SizeOf(typeof(TOKEN_PRIVILEGES));
        if (!AdjustTokenPrivileges(token, false, ref tp, (uint)size, IntPtr.Zero, IntPtr.Zero))
            return "AdjustTokenPrivileges failed " + Marshal.GetLastWin32Error();
        int err = Marshal.GetLastWin32Error();
        return err == 0 ? "SeDebugPrivilege enabled" : "AdjustTokenPrivileges err " + err + " (1300 = not held)";
    }

    public static string ReadAscii(IntPtr ptr, int len) {
        byte[] b = new byte[len];
        Marshal.Copy(ptr, b, 0, len);
        return Encoding.ASCII.GetString(b).TrimEnd('\0');
    }
}
'@

$out = New-Object System.Collections.ArrayList
function Say($s) { $out.Add($s) | Out-Null; Write-Output $s }

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Say ("host identity: {0}  elevated(Administrators)={1}" -f $id.Name, $isAdmin)
Say ("debug privilege: {0}" -f [P]::EnableDebug())

$mount = (Get-Content "$env:TEMP\opencode\wsmdiag\dd\mount.txt" -Raw).Trim()
$handlesFile = $mount.TrimEnd('\') + '\wsm\shm-handles.txt'
for ($i = 0; $i -lt 120 -and -not (Test-Path $handlesFile); $i++) { Start-Sleep -Milliseconds 1000 }
if (-not (Test-Path $handlesFile)) { Say "MISSING handles file $handlesFile"; exit 1 }
$txt = Get-Content $handlesFile -Raw
Say ("container published: " + ($txt -replace "`r?`n", ' | '))

$cpid = [int](($txt -split "`r?`n" | Where-Object { $_ -match '^pid=' }) -replace '^pid=', '')
$hNamed = [Int64](($txt -split "`r?`n" | Where-Object { $_ -match '^handle_named=' }) -replace '^handle_named=', '')
$hUnnamed = [Int64](($txt -split "`r?`n" | Where-Object { $_ -match '^handle_unnamed=' }) -replace '^handle_unnamed=', '')
Say ("container pid={0} named=0x{1:X} unnamed=0x{2:X}" -f $cpid, $hNamed, $hUnnamed)

$hp = [P]::OpenProcess(0x0040, $false, $cpid)
Say ("OpenProcess(PROCESS_DUP_HANDLE) -> 0x{0:X} err={1}" -f $hp, [Runtime.InteropServices.Marshal]::GetLastWin32Error())

$result = 'FAILED'
if ($hp -ne [IntPtr]::Zero) {
    foreach ($pair in @(@($hNamed, 'named'), @($hUnnamed, 'unnamed'))) {
        $dup = [IntPtr]::Zero
        $ok = [P]::DuplicateHandle($hp, [IntPtr]$pair[0], [P]::GetCurrentProcess(), [ref]$dup, 0, $false, 0x0002)
        Say ("  DuplicateHandle({0}, 0x{1:X}) ok={2} dup=0x{3:X} err={4}" -f $pair[1], $pair[0], $ok, $dup, [Runtime.InteropServices.Marshal]::GetLastWin32Error())
        if ($ok -and $dup -ne [IntPtr]::Zero) {
            $view = [P]::MapViewOfFile($dup, 0x0004, 0, 0, 65536)
            if ($view -ne [IntPtr]::Zero) {
                Say ("    MAP OK -> '{0}'" -f [P]::ReadAscii($view, 32))
                $result = 'OK'
                [void][P]::UnmapViewOfFile($view)
            }
            else { Say ("    MapViewOfFile FAILED err={0}" -f [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
        }
    }
    [void][P]::CloseHandle($hp)
}
Say ("RESULT: {0}" -f $result)

# keep a copy where a UAC-elevated run can still be read back
try { Set-Content -Path ($mount.TrimEnd('\') + '\wsm\host-handle-test.out') -Value ($out -join "`r`n") } catch { }
