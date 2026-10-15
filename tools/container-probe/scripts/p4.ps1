$ErrorActionPreference = 'Continue'
Write-Output '### services'
Get-Service TermService,UmRdpService,SessionEnv,Winmgmt,AppInfo -ErrorAction SilentlyContinue | Select-Object Name,DisplayName,Status,StartType | Format-Table -AutoSize | Out-String
Write-Output '### graphics/monitor system drivers'
Get-CimInstance Win32_SystemDriver -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'dxg|win32k|basicdisp|basicrender|cdd|rdp|vmbus|hyper' } | Select-Object Name,State,Started,StartMode | Format-Table -AutoSize | Out-String
Write-Output '### Session Manager SubSystems'
try { (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name SubSystems -ErrorAction Stop).SubSystems -join "`n" } catch { 'ERR ' + $_.Exception.Message }
Write-Output '### Winlogon key'
try { Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction Stop | Select-Object Shell,Userinit,DefaultUserName,DefaultDomainName,AutoAdminLogon,VmApplet | Format-List | Out-String } catch { 'ERR ' + $_.Exception.Message }
Write-Output '### HKLM\HARDWARE\DEVICEMAP\VIDEO'
try { Get-ItemProperty 'HKLM:\HARDWARE\DEVICEMAP\VIDEO' -ErrorAction Stop | Format-List | Out-String } catch { 'ERR ' + $_.Exception.Message }
Write-Output '### GraphicsDrivers key'
try { Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\GraphicsDrivers' -ErrorAction SilentlyContinue | Select-Object * -ExcludeProperty PS* | Format-List | Out-String } catch { 'ERR ' + $_.Exception.Message }
Write-Output '### Terminal Server key'
try { Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -ErrorAction SilentlyContinue | Select-Object fDenyTSConnections,TSAppAllowList,ContainerMode -ErrorAction SilentlyContinue | Format-List | Out-String } catch { 'ERR ' + $_.Exception.Message }
Write-Output '### Class\{4d36e968-e325-11ce-bfc1-08002be10318} (Display class) subkeys'
try { Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}' -ErrorAction Stop | Select-Object -ExpandProperty PSChildName } catch { 'ERR ' + $_.Exception.Message }
Write-Output '### Win32_ComputerSystem ProcessorCount/Memory'
Get-CimInstance Win32_ComputerSystem | Select-Object NumberOfProcessors,TotalPhysicalMemory | Format-List | Out-String
