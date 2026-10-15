$ErrorActionPreference = 'Continue'
Write-Output '### Win32_VideoController'
$v = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue
if ($v) { $v | Select-Object Name,VideoProcessor,AdapterRAM,Status,PNPDeviceID,DriverVersion | Format-List | Out-String } else { '(NONE - no video controller instance)' }
Write-Output '### Win32_DesktopMonitor'
$m = Get-CimInstance Win32_DesktopMonitor -ErrorAction SilentlyContinue
if ($m) { $m | Select-Object Name,ScreenWidth,ScreenHeight,Status,Availability,PNPDeviceID | Format-List | Out-String } else { '(NONE)' }
Write-Output '### Win32_DisplayControllerConfiguration'
try { $d = Get-CimInstance -ClassName Win32_DisplayControllerConfiguration -ErrorAction Stop; if ($d) { $d | Format-List | Out-String } else { '(NONE)' } } catch { 'ERR ' + $_.Exception.Message }
Write-Output '### PnP Display/Monitor/System classes'
try { Get-PnpDevice -Class Display -ErrorAction SilentlyContinue | Select-Object Status,Problem,FriendlyName,InstanceId | Format-List | Out-String } catch { 'ERR ' + $_.Exception.Message }
Write-Output '### display driver files in DriverStore (inbox basic display)'
Get-ChildItem 'C:\Windows\System32\DriverStore\FileRepository' -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'basicdisplay|basicrender|display|vmbusvideo' } | Select-Object -ExpandProperty Name
Write-Output '### System32 drivers (display related)'
Get-ChildItem 'C:\Windows\System32\drivers' -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'disp|dxg|vga|vmbus|hyper|rdp|cdd' } | Select-Object -ExpandProperty Name
