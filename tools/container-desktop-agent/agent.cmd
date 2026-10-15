@echo off
rem ---------------------------------------------------------------------------
rem Runs as the console session's shell in a process-isolated Windows container
rem (Winlogon\Shell = cmd.exe /k C:\wsm\agent.cmd). winlogon reaches the console
rem session only via a real logon, and automatically logs the session off if the
rem shell exits - hence the outer "cmd /k" that keeps a prompt alive.
rem ---------------------------------------------------------------------------

echo ==========================================================
echo  Windows container console session
echo  agent.cmd running as %USERNAME% at %DATE% %TIME%
echo ==========================================================

rem Proof of execution that the host can read back later.
echo shell started %DATE% %TIME% as %USERNAME% > C:\wsm\proof-shell.txt

rem Screenshot server: the host connects and gets a PNG (see deskcap.ps1).
start "deskcap" /min powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Port 5901 -Log C:\wsm\deskcap-5901.log

rem A visible GUI app, so a screenshot proves the desktop really draws.
start "" regedt32.exe
start "" cmd.exe /k echo second console - %USERNAME% @ %COMPUTERNAME%
