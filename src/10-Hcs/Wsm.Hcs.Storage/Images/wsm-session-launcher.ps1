# wsm-session-launcher.ps1 - start a program inside another session of this container.
#
# Why this exists: a container's rendered desktop only lives in its *console* session (the
# service session has no display device and never paints its windows - measured). Processes
# created through HCS land in the service session, and a container cannot log on (the built-in
# Administrator has a password, so winlogon stays at LogonUI and the logon hook never runs).
# This launcher closes that gap.
#
# Two phases, because the token work needs privileges only SYSTEM holds in a container
# (SeAssignPrimaryTokenPrivilege, SeTcbPrivilege - measured: the container administrator has
# neither): when it is not already running as SYSTEM it registers a one-shot scheduled task
# under SYSTEM that re-runs this same script, and prints what that task logged.
param(
    [Parameter(Mandatory = $true)][string] $Command,
    [int] $Session = 3,
    [string] $Desktop = 'winsta0\default',
    [string] $WorkingDirectory = 'C:\wsm',
    [string] $LogFile = 'C:\wsm\session-launch.log',
    [switch] $RunAsSystem
)

$ErrorActionPreference = 'Stop'
function Note([string] $m) {
    if ($script:logHandle) { $script:logHandle.WriteLine($m) }
    Write-Output $m
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class WsmSessionLauncher {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(
        IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState, int bufferLength,
        IntPtr previousState, IntPtr returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(
        IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetTokenInformation(IntPtr token, int infoClass, ref int info, int infoLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcessAsUser(
        IntPtr token, string application, string commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string currentDirectory,
        ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_USE = 0x0002 | 0x0008; // DUPLICATE | QUERY (asking for more is denied)
    public const uint TOKEN_ALL = 0x000F01FF;
    public const uint TOKEN_MAXIMUM_ALLOWED = 0x02000000;
    public const int SecurityImpersonation = 2;
    public const int TokenPrimary = 1;
    public const int TokenSessionId = 12;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
}
'@

function Test-System {
    return ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -eq 'S-1-5-18')
}

function Enable-Privilege([string] $name) {
    $token = [IntPtr]::Zero
    if (-not [WsmSessionLauncher]::OpenProcessToken(
            [WsmSessionLauncher]::GetCurrentProcess(), [WsmSessionLauncher]::TOKEN_ALL, [ref] $token)) {
        return 'token open failed'
    }

    try {
        $luid = New-Object WsmSessionLauncher+LUID
        if (-not [WsmSessionLauncher]::LookupPrivilegeValue($null, $name, [ref] $luid)) { return 'lookup failed' }

        $state = New-Object WsmSessionLauncher+TOKEN_PRIVILEGES
        $state.PrivilegeCount = 1
        $state.Luid = $luid
        $state.Attributes = 2 # SE_PRIVILEGE_ENABLED

        if (-not [WsmSessionLauncher]::AdjustTokenPrivileges(
                $token, $false, [ref] $state, 0, [IntPtr]::Zero, [IntPtr]::Zero)) { return 'adjust failed' }

        # AdjustTokenPrivileges succeeds even when the token does not hold the privilege.
        $error = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        if ($error -ne 0) { return ('not held (' + $error + ')') }
        return 'enabled'
    } finally {
        [void][WsmSessionLauncher]::CloseHandle($token)
    }
}

function Start-InSession([int] $sourcePid, [int] $sourceSession, [int] $targetSession, [string] $what) {
    $process = [WsmSessionLauncher]::OpenProcess(
        [WsmSessionLauncher]::PROCESS_QUERY_LIMITED_INFORMATION, $false, $sourcePid)
    if ($process -eq [IntPtr]::Zero) { return 'OpenProcess failed (' + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + ')' }

    try {
        $token = [IntPtr]::Zero
        if (-not [WsmSessionLauncher]::OpenProcessToken($process, [WsmSessionLauncher]::TOKEN_USE, [ref] $token)) {
            return 'OpenProcessToken failed (' + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + ')'
        }

        try {
            $primary = [IntPtr]::Zero
            if (-not [WsmSessionLauncher]::DuplicateTokenEx(
                    $token, [WsmSessionLauncher]::TOKEN_MAXIMUM_ALLOWED, [IntPtr]::Zero,
                    [WsmSessionLauncher]::SecurityImpersonation, [WsmSessionLauncher]::TokenPrimary, [ref] $primary)) {
                return 'DuplicateTokenEx failed (' + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + ')'
            }

            try {
                if ($sourceSession -ne $targetSession) {
                    $sessionId = [int] $targetSession
                    if (-not [WsmSessionLauncher]::SetTokenInformation(
                            $primary, [WsmSessionLauncher]::TokenSessionId, [ref] $sessionId, 4)) {
                        return 'SetTokenInformation(TokenSessionId) failed (' + [Runtime.InteropServices.Marshal]::GetLastWin32Error() + ')'
                    }
                }

                $environment = [IntPtr]::Zero
                [void][WsmSessionLauncher]::CreateEnvironmentBlock([ref] $environment, $primary, $false)
                try {
                    # The error is reported per variant: ERROR_PATH_NOT_FOUND (3) here means the
                    # application or the desktop could not be resolved, not a privilege problem.
                    $application = $null
                    if ($Command -match '^([^\s]+)\s') { $application = $Matches[1] }
                    $variants = @(
                        @{ App = $null;         Desktop = $Desktop; Name = 'app=null desktop=' + $Desktop },
                        @{ App = $null;         Desktop = $null;    Name = 'app=null desktop=inherit' },
                        @{ App = $application;  Desktop = $Desktop; Name = 'app=' + $application }
                    )

                    foreach ($variant in $variants) {
                        $startup = New-Object WsmSessionLauncher+STARTUPINFO
                        $startup.cb = [Runtime.InteropServices.Marshal]::SizeOf($startup)
                        $startup.lpDesktop = $variant.Desktop
                        $info = New-Object WsmSessionLauncher+PROCESS_INFORMATION

                        $created = [WsmSessionLauncher]::CreateProcessAsUser(
                            $primary, $variant.App, $Command, [IntPtr]::Zero, [IntPtr]::Zero, $false,
                            [WsmSessionLauncher]::CREATE_UNICODE_ENVIRONMENT -bor [WsmSessionLauncher]::CREATE_NEW_PROCESS_GROUP,
                            $environment, $WorkingDirectory, [ref] $startup, [ref] $info)

                        if ($created) {
                            [void][WsmSessionLauncher]::CloseHandle($info.hThread)
                            [void][WsmSessionLauncher]::CloseHandle($info.hProcess)
                            return ('ok pid=' + $info.dwProcessId + ' [' + $variant.Name + '] (from ' + $what + ')')
                        }

                        Note ('        variant ' + $variant.Name + ' -> error ' + [Runtime.InteropServices.Marshal]::GetLastWin32Error())
                    }

                    return 'CreateProcessAsUser failed for every variant'
                } finally {
                    if ($environment -ne [IntPtr]::Zero) { [void][WsmSessionLauncher]::DestroyEnvironmentBlock($environment) }
                }
            } finally {
                if ($primary -ne [IntPtr]::Zero) { [void][WsmSessionLauncher]::CloseHandle($primary) }
            }
        } finally {
            [void][WsmSessionLauncher]::CloseHandle($token)
        }
    } finally {
        [void][WsmSessionLauncher]::CloseHandle($process)
    }
}

if (-not (Test-System) -and -not $RunAsSystem) {
    # Phase one: hand over to SYSTEM through a one-shot task, then report what it logged.
    $self = $PSCommandPath
    $taskCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' + $self + '"' +
        ' -Command "' + $Command + '"' +
        ' -Session ' + $Session +
        ' -Desktop "' + $Desktop + '"' +
        ' -WorkingDirectory "' + $WorkingDirectory + '"' +
        ' -LogFile "' + $LogFile + '" -RunAsSystem'
    $launcherCmd = 'C:\wsm\wsm-session-launch-as-system.cmd'
    $content = '@echo off' + "`r`n" + $taskCommand + ' >> "' + $LogFile + '" 2>&1' + "`r`n"
    [System.IO.File]::WriteAllText($launcherCmd, $content, [System.Text.Encoding]::ASCII)
    if ([System.IO.File]::Exists($LogFile)) { [System.IO.File]::Delete($LogFile) }

    # schtasks warns on stderr about /ST being in the past, and PowerShell turns that into a
    # terminating error while ErrorActionPreference is Stop. The task is run by hand right
    # after, so the warning is irrelevant.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & schtasks.exe /create /tn 'WsmSessionLaunch' /tr $launcherCmd /sc ONCE /st 00:00 /ru SYSTEM /rl HIGHEST /f 2>&1 |
            Select-Object -Last 1 | ForEach-Object { Write-Output ('  schtasks create: ' + [string] $_) }
        & schtasks.exe /run /tn 'WsmSessionLaunch' 2>&1 |
            Select-Object -Last 1 | ForEach-Object { Write-Output ('  schtasks run: ' + [string] $_) }

        for ($i = 0; $i -lt 30 -and -not [System.IO.File]::Exists($LogFile); $i++) { Start-Sleep -Seconds 1 }
        Start-Sleep -Seconds 3
        & schtasks.exe /delete /tn 'WsmSessionLaunch' /f 2>&1 | Out-Null
    } finally {
        $ErrorActionPreference = $previousPreference
    }

    if ([System.IO.File]::Exists($LogFile)) {
        Get-Content $LogFile | ForEach-Object { Write-Output ('  | ' + $_) }
        if ((Get-Content $LogFile) -like '*launched in session*') { exit 0 }
    } else {
        Write-Output '  (the SYSTEM task produced no log)'
    }

    exit 1
}

$script:logHandle = $null
try { $script:logHandle = New-Object System.IO.StreamWriter($LogFile, $true) } catch { }

Note ('running as SYSTEM: ' + (Test-System) + ' pid=' + $PID)
foreach ($privilege in @('SeDebugPrivilege', 'SeImpersonatePrivilege', 'SeAssignPrimaryTokenPrivilege',
                         'SeIncreaseQuotaPrivilege', 'SeTcbPrivilege')) {
    Note ('  privilege ' + $privilege + ': ' + (Enable-Privilege $privilege))
}

$target = @(Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -eq $Session -and $_.Id -ne $PID } |
    Sort-Object ProcessName)
Note ("  processes in session ${Session}: " + (($target | ForEach-Object { $_.ProcessName + '(' + $_.Id + ')' }) -join ', '))

$result = $null
foreach ($candidate in $target) {
    # Same session as the source: winlogon/LogonUI live there, so no session id has to be forced.
    $result = Start-InSession $candidate.Id $Session $Session $candidate.ProcessName
    Note ('    from ' + $candidate.ProcessName + ': ' + $result)
    if ($result -like 'ok *') { break }
}

if (-not ($result -like 'ok *')) {
    Note '    no usable token in the target session; using our own and forcing the session id'
    $result = Start-InSession $PID (Get-Process -Id $PID).SessionId $Session 'self'
    Note ('    from self: ' + $result)
}

if ($result -like 'ok *') { Note ('launched in session ' + $Session + ': ' + $Command) }
else { Note ('could not launch in session ' + $Session + ': ' + $result) }

if ($script:logHandle) { $script:logHandle.Flush(); $script:logHandle.Dispose() }
if ($result -like 'ok *') { exit 0 }
exit 1
