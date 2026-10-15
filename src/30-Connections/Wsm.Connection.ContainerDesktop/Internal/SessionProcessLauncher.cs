using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace Wsm.Connection.ContainerDesktop.Internal;

/// <summary>
/// Default <see cref="ISessionProcessLauncher"/>.
/// <para>
/// When the caller is <b>SYSTEM</b> it uses the supported "service shows a window
/// on the user's desktop" mechanism: <c>WTSQueryUserToken</c> for the active
/// console session, <c>DuplicateTokenEx</c> to a primary token, then
/// <c>CreateProcessAsUser</c> with <c>lpDesktop = winsta0\default</c>. That needs
/// <c>SeTcbPrivilege</c>, which only SYSTEM holds - a UAC-elevated administrator
/// token does not, and a container never does (which is also why a container can
/// never move its own window onto the console).
/// </para>
/// <para>
/// Otherwise it degrades to starting the process in the caller's session, which is
/// exactly right when the CLI runs in the user's own session.
/// </para>
/// </summary>
internal sealed class SessionProcessLauncher : ISessionProcessLauncher, ISingletonDependency
{
    private static readonly IntPtr InvalidHandleValue = new(-1);
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint TokenAdjustSessionId = 0x0100;
    private const uint MaximumAllowed = 0x02000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    private readonly ILogger<SessionProcessLauncher> _logger;

    public SessionProcessLauncher(ILogger<SessionProcessLauncher> logger) => _logger = logger;

    /// <inheritdoc />
    public bool CanLaunchIntoInteractiveSession => IsSystem();

    /// <inheritdoc />
    public SessionProcessLaunchResult Launch(string commandLine, string? workingDirectory = null)
    {
        var session = CurrentSessionId();
        if (IsSystem())
        {
            var result = TryLaunchOnConsole(commandLine, workingDirectory);
            if (result.Launched)
            {
                return result;
            }

            _logger.LogWarning("Launching on the console session failed: {Detail}", result.Detail);
        }

        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe", "/c " + commandLine)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? Environment.SystemDirectory,
            };
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new SessionProcessLaunchResult(false, 0, session, false, "cmd.exe did not start");
            }

            return new SessionProcessLaunchResult(
                true, process.Id, session, false, $"started in the caller's session {session}");
        }
        catch (Exception ex)
        {
            return new SessionProcessLaunchResult(false, 0, session, false, ex.Message);
        }
    }

    private SessionProcessLaunchResult TryLaunchOnConsole(string commandLine, string? workingDirectory)
    {
        var ownSession = CurrentSessionId();
        var console = (int)NativeMethods.WTSGetActiveConsoleSessionId();
        if (console < 0 || console == ownSession)
        {
            return new SessionProcessLaunchResult(
                false, 0, ownSession, false, $"no separate console session (own={ownSession}, console={console})");
        }

        IntPtr userToken = IntPtr.Zero;
        if (!NativeMethods.WTSQueryUserToken((uint)console, out userToken))
        {
            return new SessionProcessLaunchResult(
                false, 0, ownSession, false, $"WTSQueryUserToken({console}) failed with {Marshal.GetLastWin32Error()}");
        }

        var environment = IntPtr.Zero;
        IntPtr primary = IntPtr.Zero;
        try
        {
            if (!NativeMethods.DuplicateTokenEx(
                    userToken, MaximumAllowed, IntPtr.Zero,
                    SecurityImpersonationLevel.SecurityImpersonation, TokenType.TokenPrimary, out primary))
            {
                return new SessionProcessLaunchResult(
                    false, 0, ownSession, false, $"DuplicateTokenEx failed with {Marshal.GetLastWin32Error()}");
            }

            _ = NativeMethods.CreateEnvironmentBlock(out environment, primary, false);

            var startup = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = @"winsta0\default",
            };

            if (!NativeMethods.CreateProcessAsUser(
                    primary, null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    CreateUnicodeEnvironment, environment, workingDirectory ?? Environment.SystemDirectory,
                    ref startup, out var processInformation))
            {
                return new SessionProcessLaunchResult(
                    false, 0, ownSession, false, $"CreateProcessAsUser failed with {Marshal.GetLastWin32Error()}");
            }

            _ = NativeMethods.ProcessIdToSessionId((uint)processInformation.dwProcessId, out var childSession);
            NativeMethods.CloseHandle(processInformation.hProcess);
            NativeMethods.CloseHandle(processInformation.hThread);
            return new SessionProcessLaunchResult(
                true, processInformation.dwProcessId, (int)childSession, true,
                $"started in the interactive session {childSession}");
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                _ = NativeMethods.DestroyEnvironmentBlock(environment);
            }

            if (primary != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(primary);
            }

            NativeMethods.CloseHandle(userToken);
        }
    }

    private static bool IsSystem()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        }
        catch
        {
            return false;
        }
    }

    private static int CurrentSessionId()
        => Process.GetCurrentProcess().SessionId;

    private enum SecurityImpersonationLevel
    {
        SecurityImpersonation = 2,
    }

    private enum TokenType
    {
        TokenPrimary = 1,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DuplicateTokenEx(
            IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes,
            SecurityImpersonationLevel impersonationLevel, TokenType tokenType, out IntPtr newToken);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcessAsUser(
            IntPtr token, string? applicationName, string commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
            IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyEnvironmentBlock(IntPtr environment);
    }
}
