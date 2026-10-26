@echo off
rem WSM desktop shell - the Winlogon\Shell payload (doc section 23.6).
rem
rem Set as HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell by
rem Wsm.Hcs.Storage:IContainerImageCustomizer.EnableDesktopSession, so winlogon starts
rem it right after the auto-logon - INSIDE the container's console session, the only
rem session with a real GDI surface.
rem
rem The shell must stay alive until agent-stop.txt is requested: an unexpected shell
rem exit makes winlogon tear the console session down with it. The read side runs in a
rem restart loop when present; otherwise PowerShell supplies the persistent wait.
setlocal
if not exist "C:\wsm" mkdir "C:\wsm" >nul 2>&1
echo %DATE% %TIME% wsm-shell starting (pid unavailable in cmd) >> "C:\wsm\shell.log"
:loop
if exist "C:\wsm\agent-stop.txt" (
    echo %DATE% %TIME% stop requested; wsm-shell exiting >> "C:\wsm\shell.log"
    exit /b 0
)
if exist "C:\wsm\wsm-desktop-read.ps1" (
    echo %DATE% %TIME% starting the read side >> "C:\wsm\shell.log"
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\wsm\wsm-desktop-read.ps1" -AsShell -AgentDirectory "C:\wsm" -MirrorFile "mirror.shm" -InputPrefix "input-" -StopFile "agent-stop.txt" -Width 1024 -Height 768 -Fps 4 -Seconds 86400 >> "C:\wsm\shell.log" 2>&1
) else (
    echo %DATE% %TIME% no read side; waiting for the stop file >> "C:\wsm\shell.log"
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "while (-not (Test-Path 'C:\wsm\agent-stop.txt')) { Start-Sleep -Seconds 1 }" >> "C:\wsm\shell.log" 2>&1
)
if exist "C:\wsm\agent-stop.txt" goto loop
echo %DATE% %TIME% shell command exited (code %ERRORLEVEL%); relaunching in 2 seconds >> "C:\wsm\shell.log"
timeout /t 2 /nobreak >nul 2>&1
goto loop
