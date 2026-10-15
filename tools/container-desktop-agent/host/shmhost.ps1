# Host-side probe: can the host reach a kernel object's HANDLE created inside the
# container's silo (OpenProcess + DuplicateHandle), and are named objects visible?
$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class HNative {
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool DuplicateHandle(IntPtr srcProc, IntPtr srcHandle,
        IntPtr dstProc, out IntPtr dstHandle, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint offHi, uint offLo, ulong length);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
    [DllImport("dwmapi.dll")] public static extern int DwmIsCompositionEnabled(out bool enabled);
    [DllImport("d3d11.dll")] public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
        uint flags, IntPtr levels, uint numLevels, uint sdk, out IntPtr device, out int featureLevel, out IntPtr context);
    public static string ReadAscii(IntPtr ptr, int len) {
        byte[] b = new byte[len];
        Marshal.Copy(ptr, b, 0, len);
        return Encoding.ASCII.GetString(b).TrimEnd('\0');
    }
}
'@

$PROCESS_DUP_HANDLE = 0x0040
$FILE_MAP_READ = 0x0004
$FILE_MAP_ALL = 0xF001F
$DUPLICATE_SAME_ACCESS = 0x0002
# The container's C:\ is the layer volume, which the host can reach directly via
# its volume GUID path (it has no drive letter). @copy uses exactly this.
$mount = (Get-Content "$env:TEMP\opencode\wsmdiag\dd\mount.txt" -Raw).Trim()
if (-not $mount) { $mount = (Get-Content "$env:TEMP\opencode\wsmdiag\dd\state.json" | ConvertFrom-Json).mount }
$handlesFile = $mount.TrimEnd('\') + '\wsm\shm-handles.txt'
Write-Output ("  container C: = " + $mount)
Write-Output ("  handles file = " + $handlesFile)

Write-Output '==== host reference points ===='
$e = $false; $hr = [HNative]::DwmIsCompositionEnabled([ref]$e)
Write-Output ("  host DwmIsCompositionEnabled hr=0x{0:X8} enabled={1}" -f $hr, $e)
$dev = [IntPtr]::Zero; $fl = 0; $ctx = [IntPtr]::Zero
$hr = [HNative]::D3D11CreateDevice([IntPtr]::Zero, 1, [IntPtr]::Zero, 0, [IntPtr]::Zero, 0, 7, [ref]$dev, [ref]$fl, [ref]$ctx)
Write-Output ("  host D3D11CreateDevice(HARDWARE) hr=0x{0:X8} device=0x{1:X}" -f $hr, $dev)
if ($dev -ne [IntPtr]::Zero) { [void][Runtime.InteropServices.Marshal]::Release($dev) }
if ($ctx -ne [IntPtr]::Zero) { [void][Runtime.InteropServices.Marshal]::Release($ctx) }

Write-Output '==== wait for the container to publish its handles ===='
for ($i = 0; $i -lt 120 -and -not (Test-Path $handlesFile); $i++) { Start-Sleep -Milliseconds 1000 }
if (-not (Test-Path $handlesFile)) { Write-Output ('  MISSING: ' + $handlesFile); exit 1 }
$txt = Get-Content $handlesFile -Raw
Write-Output ('  ' + ($txt -replace "`r?`n", ' | '))

$cpid = [int](($txt -split "`r?`n" | Where-Object { $_ -match '^pid=' }) -replace '^pid=', '')
$hNamed = [Int64](($txt -split "`r?`n" | Where-Object { $_ -match '^handle_named=' }) -replace '^handle_named=', '')
$hUnnamed = [Int64](($txt -split "`r?`n" | Where-Object { $_ -match '^handle_unnamed=' }) -replace '^handle_unnamed=', '')
Write-Output ("  container pid={0} handle_named=0x{1:X} handle_unnamed=0x{2:X}" -f $cpid, $hNamed, $hUnnamed)

Write-Output '==== test 1: is the container pid visible to the host? ===='
$p = Get-Process -Id $cpid -ErrorAction SilentlyContinue
if ($p) {
    $sess = (& tasklist.exe /fi "pid eq $cpid" /fo csv /nh 2>&1) -join ' '
    Write-Output ("  Get-Process: name={0} session={1}" -f $p.ProcessName, $p.SessionId)
    Write-Output ("  tasklist:    {0}" -f $sess)
}
else { Write-Output '  NOT VISIBLE from the host (separate PID space)' }

Write-Output '==== test 2: OpenProcess(PROCESS_DUP_HANDLE) on the container process ===='
$hp = [HNative]::OpenProcess($PROCESS_DUP_HANDLE, $false, $cpid)
$err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
Write-Output ("  OpenProcess handle=0x{0:X} err={1}" -f $hp, $err)

if ($hp -ne [IntPtr]::Zero) {
    foreach ($pair in @(@($hNamed, 'named'), @($hUnnamed, 'unnamed'))) {
        $src = [IntPtr]$pair[0]
        $dup = [IntPtr]::Zero
        $ok = [HNative]::DuplicateHandle($hp, $src, [HNative]::GetCurrentProcess(), [ref]$dup, 0, $false, $DUPLICATE_SAME_ACCESS)
        $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        Write-Output ("  DuplicateHandle({0}, 0x{1:X}) ok={2} dup=0x{3:X} err={4}" -f $pair[1], $pair[0], $ok, $dup, $err)
        if ($ok -and $dup -ne [IntPtr]::Zero) {
            $view = [HNative]::MapViewOfFile($dup, $FILE_MAP_READ, 0, 0, 65536)
            $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
            if ($view -ne [IntPtr]::Zero) {
                Write-Output ("    MAP OK -> '{0}'" -f [HNative]::ReadAscii($view, 24))
                [void][HNative]::UnmapViewOfFile($view)
            }
            else { Write-Output ("    MapViewOfFile FAILED err={0}" -f $err) }
        }
    }
    [void][HNative]::CloseHandle($hp)
}

Write-Output '==== test 3: named object visible across the silo? ===='
foreach ($name in 'Global\wsm-silo-test') {
    $h = [HNative]::OpenFileMappingW($FILE_MAP_READ, $false, $name)
    $err = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    Write-Output ("  OpenFileMapping('{0}') handle=0x{1:X} err={2}" -f $name, $h, $err)
    if ($h -ne [IntPtr]::Zero) {
        $view = [HNative]::MapViewOfFile($h, $FILE_MAP_READ, 0, 0, 65536)
        if ($view -ne [IntPtr]::Zero) {
            Write-Output ("    MAP OK -> '{0}'" -f [HNative]::ReadAscii($view, 24))
            [void][HNative]::UnmapViewOfFile($view)
        }
        [void][HNative]::CloseHandle($h)
    }
}

Write-Output '==== test 4: reverse - host creates a named object, can the container see it? ===='
$hHost = [HNative]::OpenFileMappingW($FILE_MAP_READ, $false, 'Global\wsm-silo-test')
Write-Output ("  (host OpenFileMapping of the same name = 0x{0:X} - non-zero would mean a SHARED namespace)" -f $hHost)
if ($hHost -ne [IntPtr]::Zero) { [void][HNative]::CloseHandle($hHost) }
