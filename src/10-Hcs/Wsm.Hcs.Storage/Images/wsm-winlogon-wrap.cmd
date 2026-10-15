@echo off
rem WSM window-mirror payload - the logon hook (doc section 18).
rem
rem Appended to HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Userinit by
rem Wsm.Hcs.Storage:IContainerImageCustomizer.EnableWindowMirror, so it runs INSIDE the
rem container's own interactive session (WinSta0\Default) when the container logs on.
rem That is the only place from which the container's windows can be enumerated and
rem captured: from outside, window stations are not addressable and process creation
rem into the session fails (measured - doc sections 16/17).
rem
rem This hook must never block or fail a logon, so it only launches the read side
rem detached and returns 0 immediately.
setlocal
if not exist "C:\wsm" mkdir "C:\wsm" >nul 2>&1
start "" /d "C:\wsm" cmd.exe /c wsm-desktop-read.cmd
exit /b 0
