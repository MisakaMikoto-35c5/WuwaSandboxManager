# deskinput.ps1 - input injection agent for a Windows container desktop.
#
# Companion to deskcap.ps1 (which captures the desktop). Together they are the
# only way to actually *use* the desktop of a process-isolated (Argon) container,
# because termsrv never creates an RDP listener there (see
# docs/investigation/13-container-desktop-and-rdp.md section 11).
#
# Run it the same way as deskcap.ps1: inside the container, on the session's
# active desktop, e.g. via tools/container-desktop-agent/session-run.ps1:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskinput.ps1 -Keys F3
#
# Actions:
#   info                     report window station / desktop / input desktop /
#                            cursor position / foreground window
#   move   -X -Y             move the pointer
#   click  -X -Y [-Button 2] click (1 = left, 2 = right)
#   moveclick -X -Y          click at X,Y
#   keys   -Keys 'F3'        send a key chord: F3, ENTER, ESC, CTRL+S, ALT+F4, A
#   text   -Text 'hello'     type a literal string
#   serve  -Port 5901        serve a tiny line protocol (see README) until killed
#
# Line protocol for -Port (one command per line, LF terminated):
#   M<x>,<y>            move
#   C<x>,<y>[,b]        click (b: 1 left, 2 right)
#   K<keys>             key chord
#   T<text>             type text
#   S                   take a screenshot (delegates to deskcap.ps1 -Once)
#   Q                   quit
param(
    [string] $Action = 'info',
    [int] $X = 0,
    [int] $Y = 0,
    [int] $Button = 1,
    [string] $Keys = '',
    [string] $Text = '',
    [int] $Port = 0,
    [string] $Log = 'C:\wsm\deskinput.log',

    # For -Action script: a text file with one command per line:
    #   move X,Y | click X,Y[,button] | keys CTRL+S | text literal text
    # Using a file avoids every nested-quoting problem (session-run.ps1 forwards
    # a raw command line, and PowerShell strips inner quotes on native calls).
    [string] $TextFile = ''
)

$ErrorActionPreference = 'Stop'
$dll = 'C:\wsm\Ui.dll'

$src = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class Ui {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern short VkKeyScanW(char c);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder info, int len, out int needed);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    const uint MOVE = 0x0001, LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010;
    const uint KEYUP = 0x0002;

    public static void Move(int x, int y) {
        SetCursorPos(x, y);
        mouse_event(MOVE, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(50);
    }

    public static void Click(int x, int y, int button) {
        Move(x, y);
        Thread.Sleep(80);
        if (button == 2) {
            mouse_event(RIGHTDOWN, 0, 0, 0, IntPtr.Zero); Thread.Sleep(60);
            mouse_event(RIGHTUP, 0, 0, 0, IntPtr.Zero);
        } else {
            mouse_event(LEFTDOWN, 0, 0, 0, IntPtr.Zero); Thread.Sleep(60);
            mouse_event(LEFTUP, 0, 0, 0, IntPtr.Zero);
        }
        Thread.Sleep(120);
    }

    public static void Chord(byte[] vks) {
        for (int i = 0; i < vks.Length; i++) { keybd_event(vks[i], 0, 0, IntPtr.Zero); Thread.Sleep(25); }
        Thread.Sleep(60);
        for (int i = vks.Length - 1; i >= 0; i--) { keybd_event(vks[i], 0, KEYUP, IntPtr.Zero); Thread.Sleep(25); }
        Thread.Sleep(120);
    }

    public static void Type(string s) {
        foreach (char c in s) {
            short r = VkKeyScanW(c);
            if (r == -1) continue;
            byte vk = (byte)(r & 0xff);
            bool shift = (r & 0x100) != 0, ctrl = (r & 0x200) != 0, alt = (r & 0x400) != 0;
            if (shift) keybd_event(0x10, 0, 0, IntPtr.Zero);
            if (ctrl) keybd_event(0x11, 0, 0, IntPtr.Zero);
            if (alt) keybd_event(0x12, 0, 0, IntPtr.Zero);
            keybd_event(vk, 0, 0, IntPtr.Zero); Thread.Sleep(25);
            keybd_event(vk, 0, KEYUP, IntPtr.Zero); Thread.Sleep(25);
            if (alt) keybd_event(0x12, 0, KEYUP, IntPtr.Zero);
            if (ctrl) keybd_event(0x11, 0, KEYUP, IntPtr.Zero);
            if (shift) keybd_event(0x10, 0, KEYUP, IntPtr.Zero);
        }
        Thread.Sleep(120);
    }

    public static string Foreground() {
        IntPtr h = GetForegroundWindow();
        if (h == IntPtr.Zero) return "(none)";
        StringBuilder sb = new StringBuilder(512);
        GetWindowTextW(h, sb, 512);
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        return "0x" + h.ToInt64().ToString("X") + " pid=" + pid + " '" + sb.ToString() + "'";
    }

    public static string DesktopName() {
        StringBuilder sb = new StringBuilder(256);
        int needed;
        IntPtr h = GetThreadDesktop(GetCurrentThreadId());
        // UOI_NAME = 2
        if (GetUserObjectInformationW(h, 2, sb, 512, out needed)) return sb.ToString();
        return "(unknown)";
    }
}
'@

if ((Test-Path $dll) -and ((Get-Item $dll).LastWriteTime -ge (Get-Item $PSCommandPath).LastWriteTime)) {
    Add-Type -Path $dll
} else {
    Add-Type -TypeDefinition $src -OutputAssembly $dll -OutputType Library
    Add-Type -Path $dll
}
function Write-Log([string] $m) {
    $line = ('{0} {1}' -f (Get-Date -Format 'HH:mm:ss.fff'), $m)
    Write-Output $line
    try { Add-Content -Path $Log -Value $line -ErrorAction SilentlyContinue } catch { }
}

# Log first so a failure below is still visible in the log file.
Write-Log ("--- deskinput start: action={0} port={1} pid={2}" -f $Action, $Port, $PID)

# Ui.dll is compiled once and then loaded from disk. Add-Type -Path keeps the
# file locked for the process lifetime, so another process may fail to *rebuild*
# it - fall back to an in-memory compile instead of dying (a container shell that
# dies here would leave no log at all).
$loaded = $false
if (Test-Path $dll) {
    try { Add-Type -Path $dll -ErrorAction Stop; $loaded = $true } catch { $loaded = $false }
}

if (-not $loaded) {
    try {
        Add-Type -TypeDefinition $src -Language CSharp -OutputAssembly $dll -ErrorAction Stop
        Add-Type -Path $dll -ErrorAction Stop
        Write-Log 'Ui.dll compiled'
    }
    catch {
        Write-Log ('Ui.dll rebuild failed (' + $_.Exception.Message + '); compiling in memory')
        Add-Type -TypeDefinition $src -Language CSharp
    }
}

function Get-Info {
    $cur = New-Object Ui+POINT
    [void][Ui]::GetCursorPos([ref]$cur)
    $input = [Ui]::OpenInputDesktop(0, $false, 0x0100)
    $inputErr = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
    if ($input -ne [IntPtr]::Zero) { [void][Ui]::CloseDesktop($input) }
    $wi = [Security.Principal.WindowsIdentity]::GetCurrent()
    Write-Log ("info session={0} user={1} desktop={2} inputDesktop={3} (err={4}) cursor={5},{6} foreground={7}" -f `
        $wi.Name, $wi.User.Value, [Ui]::DesktopName(), $(if ($input -ne [IntPtr]::Zero) { 'ok' } else { 'FAILED' }), $inputErr, `
        $cur.X, $cur.Y, [Ui]::Foreground())
}

$vk = @{
    'ENTER' = 0x0D; 'ESC' = 0x1B; 'TAB' = 0x09; 'SPACE' = 0x20; 'BACKSPACE' = 0x08; 'DELETE' = 0x2E;
    'UP' = 0x26; 'DOWN' = 0x28; 'LEFT' = 0x25; 'RIGHT' = 0x27; 'HOME' = 0x24; 'END' = 0x23;
    'PGUP' = 0x21; 'PGDN' = 0x22; 'INSERT' = 0x2D; 'CTRL' = 0x11; 'ALT' = 0x12; 'SHIFT' = 0x10;
    'WIN' = 0x5B; 'APPS' = 0x5D; 'PRINT' = 0x2C
}
for ($i = 1; $i -le 24; $i++) { $vk["F$i"] = 0x6F + $i }

function Resolve-Vk([string] $token) {
    $t = $token.Trim().ToUpperInvariant()
    if ($t -eq '') { return $null }
    if ($t.StartsWith('VK_')) { $t = $t.Substring(3) }
    if ($vk.ContainsKey($t)) { return [byte]$vk[$t] }
    if ($t.Length -eq 1) {
        $c = $t[0]
        if ($c -ge 'A' -and $c -le 'Z') { return [byte][char]$c }
        if ($c -ge '0' -and $c -le '9') { return [byte][char]$c }
    }
    if ($t.StartsWith('0X')) { return [byte][Convert]::ToInt32($t.Substring(2), 16) }
    return $null
}

function Send-Keys([string] $spec) {
    $parts = $spec -split '\+'
    $vks = @()
    foreach ($p in $parts) {
        $v = Resolve-Vk $p
        if ($v -ne $null) { $vks += $v }
    }
    if ($vks.Count -eq 0) { Write-Log ("keys: nothing resolved from '$spec'"); return }
    [Ui]::Chord($vks)
    Write-Log ("keys '{0}' -> {1}" -f $spec, (($vks | ForEach-Object { '0x{0:X2}' -f $_ }) -join ','))
}

function Do-Action([string] $a, [string] $arg1 = '', [int] $ax = 0, [int] $ay = 0, [int] $ab = 1, [string] $atext = '') {
    switch ($a) {
        'info' { Get-Info }
        'move' { [Ui]::Move($ax, $ay); Write-Log ("move -> {0},{1}" -f $ax, $ay) }
        'click' { [Ui]::Click($ax, $ay, $ab); Write-Log ("click {0} at {1},{2}" -f $ab, $ax, $ay) }
        'keys' { Send-Keys $arg1 }
        'text' { [Ui]::Type($atext); Write-Log ("text '{0}'" -f $atext) }
        default { Write-Log ("unknown action '$a'") }
    }
}

Get-Info

if ($Port -gt 0) {
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Any, $Port)
    $listener.Start()
    Write-Log ("serve: listening on 0.0.0.0:{0}" -f $Port)
    while ($true) {
        $client = $listener.AcceptTcpClient()
        $client.NoDelay = $true
        $stream = $client.GetStream()
        $reader = New-Object IO.StreamReader($stream)
        $writer = New-Object IO.StreamWriter($stream)
        $writer.AutoFlush = $true
        $writer.WriteLine('READY')
        try {
            while (($line = $reader.ReadLine()) -ne $null) {
                $line = $line.Trim()
                if ($line -eq '') { continue }
                if ($line -eq 'Q' -or $line -eq 'Q' + 'UIT') { $writer.WriteLine('BYE'); break }
                $op = $line.Substring(0, 1)
                $rest = $line.Substring(1)
                if ($op -eq 'M') { $xy = $rest -split ','; [Ui]::Move([int]$xy[0], [int]$xy[1]); $writer.WriteLine('OK') }
                elseif ($op -eq 'C') {
                    $xy = $rest -split ','
                    $b = 1
                    if ($xy.Count -gt 2) { $b = [int]$xy[2] }
                    [Ui]::Click([int]$xy[0], [int]$xy[1], $b); $writer.WriteLine('OK')
                }
                elseif ($op -eq 'K') { Send-Keys $rest; $writer.WriteLine('OK') }
                elseif ($op -eq 'T') { [Ui]::Type($rest); $writer.WriteLine('OK') }
                elseif ($op -eq 'S') { & 'C:\wsm\deskcap.ps1' -Once 'C:\wsm\shot.png'; $writer.WriteLine('SHOT C:\wsm\shot.png') }
                elseif ($op -eq 'I') { Get-Info; $writer.WriteLine('INFO'); }
                else { $writer.WriteLine('ERR unknown ' + $op) }
            }
        } catch { Write-Log ('serve: client error ' + $_.Exception.Message) }
        finally { try { $client.Close() } catch { } }
    }
}

# -Action script <file>: runs a list of commands (see -TextFile). One process,
# one command line - the only reliable way to drive this through session-run.ps1.
if ($Action -eq 'script') {
    if (-not $TextFile) { $TextFile = $Text }
    Write-Log ("script: {0}" -f $TextFile)
    foreach ($line in (Get-Content -LiteralPath $TextFile)) {
        $l = $line.Trim()
        if ($l -eq '' -or $l.StartsWith('#')) { continue }
        Write-Log ("script> {0}" -f $l)
        if ($l -like 'move *') {
            $xy = ($l -replace '^move\s+', '') -split ','
            [Ui]::Move([int]$xy[0], [int]$xy[1])
        }
        elseif ($l -like 'click *') {
            $xy = ($l -replace '^click\s+', '') -split ','
            $b = 1
            if ($xy.Count -gt 2) { $b = [int]$xy[2] }
            [Ui]::Click([int]$xy[0], [int]$xy[1], $b)
        }
        elseif ($l -like 'keys *') { Send-Keys ($l -replace '^keys\s+', '') }
        elseif ($l -like 'text *') { [Ui]::Type($l.Substring('text '.Length)) }
        elseif ($l -eq 'info') { Get-Info }
        else { Write-Log ("script: unknown command '{0}'" -f $l) }
    }
    Get-Info
    Write-Log '--- script done'
    exit 0
}

Do-Action $Action $Keys $X $Y $Button $Text
Get-Info
Write-Log '--- deskinput done'