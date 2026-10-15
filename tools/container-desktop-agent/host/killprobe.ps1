Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
  Where-Object { $_.CommandLine -like '*shmprobe.ps1*' } |
  ForEach-Object { Write-Output ("killing pid=" + $_.ProcessId); Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Write-Output 'done'
