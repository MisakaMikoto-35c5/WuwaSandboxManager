$ErrorActionPreference = 'Continue'
Add-Type -Name W2 -Namespace P -MemberDefinition @'
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern IntPtr GetProcessWindowStation();
[DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint t);
[DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr OpenWindowStation(string n, bool i, uint a);
[DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern IntPtr OpenDesktop(string n, uint f, bool i, uint a);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr h, int i, System.Text.StringBuilder p, uint n, out uint need);
[DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
[DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int index);
[DllImport("gdi32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr hdc);
'@
function NameOf($h) { if ($h -eq [IntPtr]::Zero) { return '(null)' }; $sb = New-Object System.Text.StringBuilder 256; $n = [uint32]0; [void][P.W2]::GetUserObjectInformation($h, 2, $sb, 256, [ref]$n); return $sb.ToString() }
$tid = [P.W2]::GetCurrentThreadId()
Write-Output ('current thread id = {0}' -f $tid)
Write-Output ('process window station = {0}' -f (NameOf ([P.W2]::GetProcessWindowStation())))
$dt = [P.W2]::GetThreadDesktop($tid)
Write-Output ('thread desktop = {0}' -f (NameOf $dt))
foreach ($n in 'WinSta0','Service-0x0-3e7$','NoSuchSta12345') {
  $h = [P.W2]::OpenWindowStation($n, $false, 0x0002)  # WINSTA_READATTRIBUTES
  $e = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
  Write-Output ('OpenWindowStation("{0}") = 0x{1:X} err={2}' -f $n, $h.ToInt64(), $e)
}
foreach ($n in 'Default','Winlogon','Disconnect','Screen-saver') {
  $h = [P.W2]::OpenDesktop($n, 0, $false, 0x0001)
  $e = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
  Write-Output ('OpenDesktop("{0}") = 0x{1:X} err={2}' -f $n, $h.ToInt64(), $e)
}
$hdc = [P.W2]::GetDC([IntPtr]::Zero)
if ($hdc -ne [IntPtr]::Zero) {
  Write-Output ('GetDC(NULL) OK -> HORZRES={0} VERTRES={1} BITSPIXEL={2} PLANES={3} NUMCOLORS={4} TECHNOLOGY={5} (0=unset,1=VGA,2=vector,4=raster,6=display) VREFRESH={6} NUMCOLORS2={7}' -f ([P.W2]::GetDeviceCaps($hdc,8)),([P.W2]::GetDeviceCaps($hdc,10)),([P.W2]::GetDeviceCaps($hdc,12)),([P.W2]::GetDeviceCaps($hdc,14)),([P.W2]::GetDeviceCaps($hdc,24)),([P.W2]::GetDeviceCaps($hdc,18)),([P.W2]::GetDeviceCaps($hdc,116)),([P.W2]::GetDeviceCaps($hdc,24)))
  [void][P.W2]::ReleaseDC([IntPtr]::Zero, $hdc)
} else { Write-Output 'GetDC(NULL) = NULL' }
Write-Output '--- System32 user32/gdi32 mapped modules check ---'
Get-Process -Id $PID | Select-Object -ExpandProperty Modules | Where-Object { $_.ModuleName -match 'win32k|gdi32|user32|dwmapi|cdd|dxgi' } | Select-Object ModuleName,FileName | Format-Table -AutoSize | Out-String
