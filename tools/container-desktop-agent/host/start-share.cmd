@echo off
rem NOTE: -Visible matters. A process created without a console (CREATE_NO_WINDOW)
rem gets a black TSDDD framebuffer from CopyFromScreen in the container, while
rem GetForegroundWindow still sees the real windows. Visible => real pixels.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\session-run.ps1 -Session 4 -Desktop winsta0\Winlogon -Visible -CommandLine "cmd.exe /c C:\wsm\share-start.cmd"
