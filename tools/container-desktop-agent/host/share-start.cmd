@echo off
rem Drop /min: the container's TSDDD virtual display gives a black framebuffer to
rem a process whose console is minimized/hidden (CopyFromScreen no-ops), while a
rem normal visible console captures the real desktop.
del C:\wsm\frame.shm 2>nul
del C:\wsm\deskshare.out 2>nul
start "" cmd.exe /c "powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskshare.ps1 -Seconds 120 > C:\wsm\deskshare.out 2>&1"
