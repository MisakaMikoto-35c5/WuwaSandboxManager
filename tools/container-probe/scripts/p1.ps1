$ErrorActionPreference = 'Continue'
Write-Output '### OS'; Get-CimInstance Win32_OperatingSystem | Select-Object Caption,Version,BuildNumber,ProductType,OSArchitecture | Format-List | Out-String
Write-Output '### processes'; Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,SessionId,Name | Sort-Object SessionId,Name | Format-Table -AutoSize | Out-String -Width 200
Write-Output '### logon sessions (type: 2=interactive 3=network 4=batch 5=service)'; Get-CimInstance Win32_LogonSession | Select-Object LogonId,LogonType,StartTime | Format-Table -AutoSize | Out-String -Width 200
Write-Output '### computer system'; Get-CimInstance Win32_ComputerSystem | Select-Object Name,UserName,Domain,Model,Manufacturer | Format-List | Out-String
