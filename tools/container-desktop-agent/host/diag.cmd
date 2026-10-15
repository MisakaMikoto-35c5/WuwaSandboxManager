@echo off
del C:\wsm\j1.* 2>nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskinput.ps1 -Action info -Log C:\wsm\j1.log > C:\wsm\j1.out 2> C:\wsm\j1.err
echo exit=%errorlevel% > C:\wsm\j1.done
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\j1.png -Log C:\wsm\j2.log >> C:\wsm\j1.out 2>> C:\wsm\j1.err
echo exit2=%errorlevel% >> C:\wsm\j1.done
