<#
.SYNOPSIS
  The console session's shell in a process-isolated Windows container.

.DESCRIPTION
  winlogon starts this (Winlogon\Shell) after a successful logon, so it runs IN
  the console session - the only session with a desktop (a TSDDD framebuffer,
  1024x768x32) and the one where winlogon/LogonUI live.

  Notes learned the hard way:
    * Shell must be a real executable command line. Pointing Shell at a .cmd file
      makes winlogon fail to start it and log the session straight off.
    * The shell process must not exit, or winlogon logs the user off (and with
      ForceAutoLogon it loops). Serving screenshots blocks, so this script stays
      alive on purpose.

  It starts regedt32.exe (a visible GUI app, so a screenshot proves the desktop
  draws) and a console, then serves screenshots over TCP.
#>
[CmdletBinding()]
param(
    [int] $Port = 5901,
    [string] $Log = 'C:\wsm\deskcap-5901.log'
)

$ErrorActionPreference = 'Continue'

New-Item -ItemType Directory -Force 'C:\wsm' | Out-Null

# Visible proof that the desktop is real: the registry editor and a console.
try { Start-Process 'C:\Windows\System32\regedt32.exe' } catch { }
try { Start-Process 'C:\Windows\System32\cmd.exe' -ArgumentList '/k', 'echo Windows container console session' } catch { }

# Blocks (that is the point: the shell must stay alive) and serves PNGs.
& 'C:\wsm\deskcap.ps1' -Port $Port -Log $Log
