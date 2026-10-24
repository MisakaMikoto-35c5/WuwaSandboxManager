@echo off
rem WSM desktop shell - the Winlogon\Shell payload (doc section 23.6).
rem
rem Set as HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell by
rem Wsm.Hcs.Storage:IContainerImageCustomizer.EnableDesktopSession, so winlogon starts
rem it right after the auto-logon - INSIDE the container's console session, the only
rem session with a real GDI surface.
rem
rem The shell must NEVER exit: a shell that terminates (plain cmd.exe reads EOF and
rem quits) makes winlogon tear the console session down with it. So the read side runs
rem here in a restart loop: when it crashes or stops, wait 2 seconds and relaunch.
rem The read side is invoked with -AsShell so it stays in the foreground of this wrapper
rem instead of detaching (the wrapper IS the session's lifetime).
setlocal
if not exist "C:\wsm" mkdir "C:\wsm" >nul 2>&1
echo %DATE% %TIME% wsm-shell starting (pid unavailable in cmd) >> "C:\wsm\shell.log"
:loop
echo %DATE% %TIME% starting the read side >> "C:\wsm\shell.log"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\wsm\wsm-desktop-read.ps1" -AsShell -AgentDirectory "C:\wsm" -MirrorFile "mirror.shm" -InputPrefix "input-" -StopFile "agent-stop.txt" -Width 1024 -Height 768 -Fps 4 -Seconds 86400 >> "C:\wsm\shell.log" 2>&1
echo %DATE% %TIME% read side exited (code %ERRORLEVEL%); relaunching in 2 seconds >> "C:\wsm\shell.log"
timeout /t 2 /nobreak >nul 2>&1
goto loop
