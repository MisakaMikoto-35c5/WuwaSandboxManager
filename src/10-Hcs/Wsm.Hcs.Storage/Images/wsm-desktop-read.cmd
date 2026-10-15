@echo off
rem Runs the window-mirror read side (wsm-desktop-read.ps1) in the container's session.
rem
rem The console must be VISIBLE (no /min, no CREATE_NO_WINDOW): a hidden or minimized
rem process gets a black TSDDD frame out of CopyFromScreen, which looks exactly like
rem "capture is broken". Quoting lives here (a plain .cmd) so the starter can stay free
rem of nested quotes.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\wsm\wsm-desktop-read.ps1" -AgentDirectory "C:\wsm" -MirrorFile "mirror.shm" -InputPrefix "input-" -StopFile "agent-stop.txt" -Width 1024 -Height 768 -Fps 4 -Seconds 86400 > "C:\wsm\read.out" 2>&1
