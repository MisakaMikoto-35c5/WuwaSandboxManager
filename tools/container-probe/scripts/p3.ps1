$ErrorActionPreference = 'Continue'
Add-Type -Name W -Namespace P -MemberDefinition @'
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern IntPtr GetProcessWindowStation();
[DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint t);
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr OpenInputDesktop(uint f, bool i, uint a);
[DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr OpenWindowStation(string n, bool i, uint a);
[DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr OpenDesktop(string n, uint f, bool i, uint a);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr h, int i, System.Text.StringBuilder p, uint n, out uint need);
[DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
[DllImport("user32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int index);
[DllImport("user32.dll", SetLastError=true)] public static extern bool SwitchDesktop(IntPtr h);
'@
function NameOf($h) {
  if ($h -eq [IntPtr]::Zero) { return '(null)' }
  $sb = New-Object System.Text.StringBuilder 256; $n = [uint32]0
  [void][P.W]::GetUserObjectInformation($h, 2, $sb, 256, [ref]$n)
  return $sb.ToString()
}
$tid = [uint32][System.Threading.Thread]::CurrentThread.ManagedThreadId
$ws = [P.W]::GetProcessWindowStation()
$dt = [P.W]::GetThreadDesktop($tid)
Write-Output ('SM_CXSCREEN={0} SM_CYSCREEN={1} SM_CMONITORS={2} SM_CXVIRTUALSCREEN={3} SM_CYVIRTUALSCREEN={4}' -f ([P.W]::GetSystemMetrics(0)),([P.W]::GetSystemMetrics(1)),([P.W]::GetSystemMetrics(80)),([P.W]::GetSystemMetrics(78)),([P.W]::GetSystemMetrics(79)))
Write-Output ('process window station = {0}' -f (NameOf $ws))
Write-Output ('thread desktop         = {0}' -f (NameOf $dt))
$id = [P.W]::OpenInputDesktop(0, $false, 0x0001)
Write-Output ('OpenInputDesktop("Default") = 0x{0:X} err={1}' -f $id.ToInt64(), [Runtime.InteropServices.Marshal]::GetLastWin32Error())
$win = [P.W]::OpenWindowStation('WinSta0', $false, 0x0001)
$e1 = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
Write-Output ('OpenWindowStation("WinSta0") = 0x{0:X} err={1}' -f $win.ToInt64(), $e1)
$dsk = [P.W]::OpenDesktop('Default', 0, $false, 0x0001)
$e2 = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
Write-Output ('OpenDesktop("Default") = 0x{0:X} err={1}' -f $dsk.ToInt64(), $e2)
$hdc = [P.W]::GetDC([IntPtr]::Zero)
if ($hdc -ne [IntPtr]::Zero) {
  Write-Output ('GetDC(NULL) OK -> HORZRES={0} VERTRES={1} BITSPIXEL={2} PLANES={3} NUMCOLORS={4} TECHNOLOGY={5} (0=unset/1=vga 2=vector 4=raster 6=display)' -f ([P.W]::GetDeviceCaps($hdc,8)),([P.W]::GetDeviceCaps($hdc,10)),([P.W]::GetDeviceCaps($hdc,12)),([P.W]::GetDeviceCaps($hdc,14)),([P.W]::GetDeviceCaps($hdc,24)),([P.W]::GetDeviceCaps($hdc,18)))
} else { Write-Output 'GetDC(NULL) = NULL (no display device)' }
