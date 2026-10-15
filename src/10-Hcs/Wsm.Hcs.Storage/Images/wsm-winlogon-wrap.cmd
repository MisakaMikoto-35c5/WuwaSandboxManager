@echo off
rem WSM window-mirror payload - the logon hook (doc section 18).
rem
rem Appended to HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Userinit by
rem Wsm.Hcs.Storage:IContainerImageCustomizer.EnableWindowMirror, so it runs INSIDE the
rem container's own interactive session (WinSta0\Default) when the container logs on -
rem the only place from which the container's windows can be enumerated and captured.
rem
rem It launches the read side, which detaches itself (see -Detached in
rem wsm-desktop-read.ps1), so this hook returns in about a second and can never hold up a
rem logon. No cmd `start` is used: that kept the launcher process alive for the child's
rem lifetime, which parked HCS process creation for its whole timeout.
setlocal
if not exist "C:\wsm" mkdir "C:\wsm" >nul 2>&1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\wsm\wsm-desktop-read.ps1" -AgentDirectory "C:\wsm" -MirrorFile "mirror.shm" -InputPrefix "input-" -StopFile "agent-stop.txt" -Width 1024 -Height 768 -Fps 4 -Seconds 86400
exit /b 0
