<#
.SYNOPSIS
  Captures the desktop of the session this script runs in and serves it over TCP
  as a length-prefixed PNG per connection, so a host can watch / screenshot a
  Windows container's console session without RDP.

.DESCRIPTION
  Run this INSIDE the target session and desktop - use session-run.ps1 to start
  it there - because GDI captures the desktop the calling thread is attached to.
  On a container's console session that is the TSDDD ("Terminal Server Device
  Driver") offscreen framebuffer: there is no physical display, but the desktop
  is real and draws normally.

  Each accepted connection gets a freshly captured PNG:
      [4 bytes little-endian length][PNG bytes]

.PARAMETER Port
  TCP port to listen on (default 5901).

.EXAMPLE
  # from inside the console session
  powershell -NoProfile -File C:\wsm\deskcap.ps1 -Port 5901
#>
[CmdletBinding()]
param(
    [int] $Port = 5901,
    [string] $Log = 'C:\wsm\deskcap.log',

    # Capture once to this path and exit, instead of serving over TCP. Useful when
    # the host cannot reach the container's port (the host then reads the file out
    # of the container, e.g. as base64).
    [string] $Once = ''
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

Add-Type -Namespace DeskCap -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
[DllImport("user32.dll")] public static extern IntPtr GetProcessWindowStation();
[DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(int threadId);
[DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(int flags, bool inherit, int access);
[DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr handle, int index, System.Text.StringBuilder info, int length, out int needed);
[DllImport("kernel32.dll")] public static extern int GetCurrentThreadId();
'@ | Out-Null

function Get-ObjectName([IntPtr] $handle) {
    if ($handle -eq [IntPtr]::Zero) { return '(null)' }
    $buffer = New-Object System.Text.StringBuilder 256
    $needed = 0
    if ([DeskCap.Native]::GetUserObjectInformation($handle, 2, $buffer, 512, [ref] $needed)) {
        return $buffer.ToString()
    }
    return '(err ' + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + ')'
}

$width = [DeskCap.Native]::GetSystemMetrics(0)
$height = [DeskCap.Native]::GetSystemMetrics(1)
$windowStation = Get-ObjectName ([DeskCap.Native]::GetProcessWindowStation())
$desktop = Get-ObjectName ([DeskCap.Native]::GetThreadDesktop([DeskCap.Native]::GetCurrentThreadId()))
$inputDesktop = [DeskCap.Native]::OpenInputDesktop(0, $false, 0x0001)   # DESKTOP_READOBJECTS
$inputName = Get-ObjectName $inputDesktop
if ($inputDesktop -ne [IntPtr]::Zero) { [void][DeskCap.Native]::CloseDesktop($inputDesktop) }

$session = (Get-Process -Id $PID).SessionId
$banner = ("deskcap pid={0} session={1} winsta={2} desktop={3} inputDesktop={4} screen={5}x{6} port={7}" -f `
        $PID, $session, $windowStation, $desktop, $inputName, $width, $height, $Port)

New-Item -ItemType Directory -Force -Path (Split-Path $Log -Parent) | Out-Null
Add-Content -Path $Log -Value $banner
Write-Output $banner

function Save-Shot {
    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size($width, $height)),
            [System.Drawing.CopyPixelOperation]::SourceCopy)
    }
    finally {
        $graphics.Dispose()
    }

    $stream = New-Object System.IO.MemoryStream
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
        $bitmap.Dispose()
    }
}

if ($Once) {
    # One-shot: capture, write the PNG, exit. No listener, so nothing has to be
    # reachable from the host - the host reads the file out of the container.
    $bytes = Save-Shot
    New-Item -ItemType Directory -Force -Path (Split-Path $Once -Parent) | Out-Null
    [IO.File]::WriteAllBytes($Once, $bytes)
    Write-Output ("deskcap: wrote {0} ({1:N0} bytes)" -f $Once, $bytes.Length)
    return
}

$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Any, $Port)
$listener.Start()
while ($true) {
    $client = $listener.AcceptTcpClient()
    try {
        $bytes = Save-Shot
        $networkStream = $client.GetStream()
        $length = [BitConverter]::GetBytes([int] $bytes.Length)
        $networkStream.Write($length, 0, 4)
        $networkStream.Write($bytes, 0, $bytes.Length)
        $networkStream.Flush()
        Add-Content -Path $Log -Value ("sent {0} bytes to {1}" -f $bytes.Length, $client.Client.RemoteEndPoint)
    }
    catch {
        Add-Content -Path $Log -Value ("error: " + $_.Exception.Message)
    }
    finally {
        $client.Close()
    }
}
