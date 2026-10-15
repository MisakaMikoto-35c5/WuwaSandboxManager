$ErrorActionPreference = 'Continue'
function H($t) { Write-Output ''; Write-Output ('===== ' + $t + ' =====') }

H 'OS'
try { Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,ProductType | Format-List | Out-String } catch { 'ERR ' + $_ }

H 'processes (pid, ppid, session, name)'
try { Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,SessionId,Name | Sort-Object SessionId,Name | Format-Table -AutoSize | Out-String -Width 200 } catch { 'ERR ' + $_ }

H 'logon sessions'
try { Get-CimInstance Win32_LogonSession | Select-Object LogonId,LogonType,StartTime | Format-Table -AutoSize | Out-String -Width 200 } catch { 'ERR ' + $_ }

H 'video controllers (Win32_VideoController)'
try { $v = Get-CimInstance Win32_VideoController; if ($v) { $v | Select-Object Name,VideoProcessor,AdapterRAM,Status,PNPDeviceID | Format-List | Out-String } else { '(none)' } } catch { 'ERR ' + $_ }

H 'desktop monitors (Win32_DesktopMonitor)'
try { $m = Get-CimInstance Win32_DesktopMonitor; if ($m) { $m | Select-Object Name,ScreenWidth,ScreenHeight,Status,Availability | Format-List | Out-String } else { '(none)' } } catch { 'ERR ' + $_ }

H 'PnP class Display'
try { $d = Get-PnpDevice -Class Display -ErrorAction SilentlyContinue; if ($d) { $d | Select-Object Status,Problem,FriendlyName,InstanceId | Format-List | Out-String } else { '(none)' } } catch { 'ERR ' + $_ }

H 'PnP class Monitor'
try { $p = Get-PnpDevice -Class Monitor -ErrorAction SilentlyContinue; if ($p) { $p | Select-Object Status,FriendlyName | Format-List | Out-String } else { '(none)' } } catch { 'ERR ' + $_ }

H 'window station + metrics'
try {
  Add-Type -Name W -Namespace P -MemberDefinition @'
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
[DllImport("user32.dll")] public static extern IntPtr GetProcessWindowStation();
[DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint t);
[DllImport("user32.dll", SetLastError=true)] public static extern IntPtr OpenInputDesktop(uint f, bool i, uint a);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr h, int i, System.Text.StringBuilder p, uint n, out uint need);
[DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
[DllImport("user32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int index);
'@
  $tid = [uint32][System.Threading.Thread]::CurrentThread.ManagedThreadId
  $ws = [P.W]::GetProcessWindowStation()
  $dt = [P.W]::GetThreadDesktop($tid)
  $sb = New-Object System.Text.StringBuilder 256; $n = [uint32]0
  [void][P.W]::GetUserObjectInformation($ws, 2, $sb, 256, [ref]$n); $wsName = $sb.ToString()
  $sb2 = New-Object System.Text.StringBuilder 256; $n2 = [uint32]0
  [void][P.W]::GetUserObjectInformation($dt, 2, $sb2, 256, [ref]$n2); $dtName = $sb2.ToString()
  $id = [P.W]::OpenInputDesktop(0, $false, 0x0001)
  $hdc = [P.W]::GetDC([IntPtr]::Zero)
  'SM_CXSCREEN={0} SM_CYSCREEN={1} SM_CMONITORS={2} SM_CXVIRTUALSCREEN={3} SM_CYVIRTUALSCREEN={4}' -f ([P.W]::GetSystemMetrics(0)),([P.W]::GetSystemMetrics(1)),([P.W]::GetSystemMetrics(80)),([P.W]::GetSystemMetrics(78)),([P.W]::GetSystemMetrics(79))
  'windowstation name = {0} (handle 0x{1:X})' -f $wsName, $ws.ToInt64()
  'thread desktop name = {0} (handle 0x{1:X})' -f $dtName, $dt.ToInt64()
  'OpenInputDesktop handle = 0x{0:X} (0 = none/denied)' -f $id.ToInt64()
  if ($hdc -ne [IntPtr]::Zero) {
    'GetDC(NULL) ok; HORZRES={0} VERTRES={1} BITSPIXEL={2} NUMCOLORS={3} TECHNOLOGY={4}' -f ([P.W]::GetDeviceCaps($hdc,8)),([P.W]::GetDeviceCaps($hdc,10)),([P.W]::GetDeviceCaps($hdc,12)),([P.W]::GetDeviceCaps($hdc,24)),([P.W]::GetDeviceCaps($hdc,18))
  } else { 'GetDC(NULL) returned NULL (no display device)' }
} catch { 'ERR ' + $_ }

H 'services of interest'
try { Get-Service TermService,UmRdpService,SessionEnv,AppInfo,Winmgmt -ErrorAction SilentlyContinue | Select-Object Name,Status,StartType | Format-Table -AutoSize | Out-String } catch { 'ERR ' + $_ }

H 'display registry'
try { Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -ErrorAction SilentlyContinue | Format-List | Out-String } catch { 'ERR ' + $_ }
try { (Get-ItemProperty 'HKLM:\HARDWARE\DEVICEMAP\VIDEO' -ErrorAction SilentlyContinue) | Format-List | Out-String } catch { 'ERR ' + $_ }

H 'end'
