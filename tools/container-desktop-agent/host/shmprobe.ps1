$ErrorActionPreference = 'Continue'

Write-Output '==== identity ===='
$wi = [Security.Principal.WindowsIdentity]::GetCurrent()
Write-Output ("  pid={0} pid_user={1} user={2} sid={3}" -f $PID, $PID, $wi.Name, $wi.User.Value)

Write-Output '==== DWM / shell / RDP processes ===='
Get-Process dwm, explorer, sihost, winlogon, LogonUI, rdpclip, CExecSvc, csrss -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Output ("  {0,-14} pid={1,-7} session={2}" -f $_.ProcessName, $_.Id, $_.SessionId) }
Write-Output ("  dwm.exe file present = {0}" -f (Test-Path "$env:SystemRoot\System32\dwm.exe"))

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Native {
    [DllImport("dwmapi.dll")] public static extern int DwmIsCompositionEnabled(out bool enabled);
    [DllImport("d3d11.dll")] public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
        uint flags, IntPtr levels, uint numLevels, uint sdk, out IntPtr device, out int featureLevel, out IntPtr context);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr sa, uint prot, uint maxHi, uint maxLo, string name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint offHi, uint offLo, ulong length);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UnmapViewOfFile(IntPtr p);
}
'@

Write-Output '==== DWM composition ===='
$enabled = $false
$hr = [Native]::DwmIsCompositionEnabled([ref]$enabled)
Write-Output ("  DwmIsCompositionEnabled hr=0x{0:X8} enabled={1}" -f $hr, $enabled)

Write-Output '==== D3D11 devices ===='
foreach ($dt in @(@(1, 'HARDWARE'), @(5, 'WARP'), @(0, 'UNKNOWN'))) {
    $dev = [IntPtr]::Zero; $fl = 0; $ctx = [IntPtr]::Zero
    $hr = [Native]::D3D11CreateDevice([IntPtr]::Zero, $dt[0], [IntPtr]::Zero, 0, [IntPtr]::Zero, 0, 7, [ref]$dev, [ref]$fl, [ref]$ctx)
    Write-Output ("  driverType={0,-8} hr=0x{1:X8} device=0x{2:X} featureLevel=0x{3:X}" -f $dt[1], $hr, $dev, $fl)
    if ($dev -ne [IntPtr]::Zero) { [void][Runtime.InteropServices.Marshal]::Release($dev) }
    if ($ctx -ne [IntPtr]::Zero) { [void][Runtime.InteropServices.Marshal]::Release($ctx) }
}

Write-Output '==== display adapters (EnumDisplayDevices) ===='
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Disp {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool EnumDisplayDevices(string device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);
}
'@
$dd = New-Object Disp+DISPLAY_DEVICE
$dd.cb = [Runtime.InteropServices.Marshal]::SizeOf($dd)
for ($i = 0; $i -lt 8; $i++) {
    if (-not [Disp]::EnumDisplayDevices($null, $i, [ref]$dd, 0)) { break }
    Write-Output ("  [{0}] name={1}  adapter='{2}' flags=0x{3:X}" -f $i, $dd.DeviceName, $dd.DeviceString, $dd.StateFlags)
}

Write-Output '==== WinRT Windows.Graphics.Capture ===='
try {
    $t = [Windows.Graphics.Capture.GraphicsCaptureItem, Windows.Graphics.Capture, ContentType = WindowsRuntime]
    Write-Output ("  type activated: {0}" -f $t.FullName)
    $c = [Windows.Graphics.Capture.GraphicsCaptureSession, Windows.Graphics.Capture, ContentType = WindowsRuntime]
    Write-Output ("  session type:   {0}" -f $c.FullName)
    Write-Output ("  IsSupported:    {0}" -f [Windows.Graphics.Capture.GraphicsCaptureSession]::IsSupported)
}
catch { Write-Output ("  WGC unavailable: " + $_.Exception.Message) }

Write-Output '==== create shared sections and publish handles ===='
$PAGE_READWRITE = 0x04
$FILE_MAP_ALL = 0xF001F

$named = [Native]::CreateFileMappingW([IntPtr](-1), [IntPtr]::Zero, $PAGE_READWRITE, 0, 65536, 'Global\wsm-silo-test')
$errNamed = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
$viewNamed = [IntPtr]::Zero
if ($named -ne [IntPtr]::Zero) {
    $viewNamed = [Native]::MapViewOfFile($named, $FILE_MAP_ALL, 0, 0, 65536)
    if ($viewNamed -ne [IntPtr]::Zero) {
        $bytes = [Text.Encoding]::ASCII.GetBytes('WSM-SHM-NAMED-OK')
        [Runtime.InteropServices.Marshal]::Copy($bytes, 0, $viewNamed, $bytes.Length)
    }
}
Write-Output ("  named   handle=0x{0:X} err={1} view=0x{2:X}" -f $named, $errNamed, $viewNamed)

$unnamed = [Native]::CreateFileMappingW([IntPtr](-1), [IntPtr]::Zero, $PAGE_READWRITE, 0, 131072, $null)
$errUnnamed = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
$viewUnnamed = [IntPtr]::Zero
if ($unnamed -ne [IntPtr]::Zero) {
    $viewUnnamed = [Native]::MapViewOfFile($unnamed, $FILE_MAP_ALL, 0, 0, 131072)
    if ($viewUnnamed -ne [IntPtr]::Zero) {
        $bytes = [Text.Encoding]::ASCII.GetBytes('WSM-SHM-UNNAMED-OK')
        [Runtime.InteropServices.Marshal]::Copy($bytes, 0, $viewUnnamed, $bytes.Length)
    }
}
Write-Output ("  unnamed handle=0x{0:X} err={1} view=0x{2:X}" -f $unnamed, $errUnnamed, $viewUnnamed)

$out = "pid=$PID`nhandle_named=$($named.ToInt64())`nhandle_unnamed=$($unnamed.ToInt64())`nbyname=none"
Set-Content -Path 'C:\wsm\shm-handles.txt' -Value $out
Write-Output ('  published: ' + ((Get-Content 'C:\wsm\shm-handles.txt' -Raw) -replace "`n", ' | '))

Write-Output '==== holding the sections alive for 90s ===='
Start-Sleep -Seconds 90
Write-Output '==== done ===='
