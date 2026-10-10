using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using WindowsSandboxManager.Domain.Abstractions;

namespace WindowsSandboxManager.Infrastructure.Environment;

/// <summary>
/// Describes the host Windows Sandbox installation. It reproduces the detection
/// logic used by Microsoft's own <c>SandboxContracts</c> / <c>GrpcServer</c>
/// so that we agree with the platform about availability, multi-instance policy
/// and the control-plane pipe name.
/// </summary>
public sealed class WindowsSandboxEnvironment : ISandboxEnvironment
{
    private const string WvdEnabledValueName = "TerminalServices-RemoteConnectionManager-WVD-Enabled";

    private const string SystemDirectoryRelativeExe = "WindowsSandbox.exe";
    private const string RemoteSessionAlias = "WindowsSandboxRemoteSession.exe";
    private const string ServerAlias = "WindowsSandboxServer.exe";

    private readonly Lazy<string?> _packagePath;
    private readonly Lazy<bool> _isSupported;

    public WindowsSandboxEnvironment()
    {
        _packagePath = new Lazy<string?>(ResolvePackagePath);
        _isSupported = new Lazy<bool>(ResolveIsSupported);
    }

    public bool IsSupported => _isSupported.Value;

    public bool IsElevated
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    public bool IsMultipleInstancesAllowed => IsWvdEnabled() || IsInternalBranch();

    public string? PackageInstallPath => _packagePath.Value;

    public string? ServerPipeName
    {
        get
        {
            var sid = WindowsIdentity.GetCurrent().User;
            if (sid is null)
            {
                return null;
            }

            var hash = MD5.HashData(Encoding.UTF8.GetBytes(sid.ToString()));
            var sessionId = new Guid(hash).ToString();
            // Matches GrpcServer.PipeName => Path.Combine("wsandbox", UserSessionId)
            return Path.Combine("wsandbox", sessionId);
        }
    }

    public string? LegacyExecutablePath
    {
        get
        {
            var path = Path.Combine(
                System.Environment.SystemDirectory,
                SystemDirectoryRelativeExe);
            return File.Exists(path) ? path : null;
        }
    }

    public string? RemoteSessionExecutablePath => ResolveAlias(RemoteSessionAlias);

    public string? ServerExecutablePath => ResolveAlias(ServerAlias);

    /// <summary>
    /// Ensures the control-plane server process is running. Mirrors
    /// <c>GrpcServer.IsRunning()/Start()</c>.
    /// </summary>
    public bool EnsureServerRunning()
    {
        var pipeName = ServerPipeName;
        if (pipeName is null)
        {
            return false;
        }

        var pipePath = Path.Combine(@"\\.\pipe\", pipeName);
        if (PipeExists(pipePath))
        {
            return true;
        }

        var exe = ServerExecutablePath ?? ServerAlias;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
            });

            // Give the server a moment to create the pipe.
            for (var i = 0; i < 30; i++)
            {
                if (PipeExists(pipePath))
                {
                    return true;
                }

                Thread.Sleep(200);
            }
        }
        catch
        {
            return false;
        }

        return PipeExists(pipePath);
    }

    private static bool PipeExists(string pipePath)
    {
        try
        {
            return Directory.GetFiles(@"\\.\pipe\").Contains(pipePath, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool ResolveIsSupported()
    {
        if (LegacyExecutablePath is not null)
        {
            return true;
        }

        return PackageInstallPath is not null;
    }

    private static string? ResolveAlias(string alias)
    {
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var candidate = Path.Combine(local, "Microsoft", "WindowsApps", alias);
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? ResolvePackagePath()
    {
        // Enumerating C:\Program Files\WindowsApps is denied for unelevated
        // processes, so resolve the package through its AppExecutionAlias
        // reparse point instead.
        foreach (var alias in new[] { ServerAlias, RemoteSessionAlias })
        {
            var aliasPath = ResolveAlias(alias);
            if (aliasPath is null)
            {
                continue;
            }

            try
            {
                var target = File.ResolveLinkTarget(aliasPath, returnFinalTarget: true);
                if (target is null)
                {
                    continue;
                }

                var current = target is FileInfo ? Path.GetDirectoryName(target.FullName) : target.FullName;
                for (var i = 0; i < 8 && current is not null; i++)
                {
                    if (File.Exists(Path.Combine(current, "AppxManifest.xml")))
                    {
                        return current;
                    }

                    current = Path.GetDirectoryName(current);
                }
            }
            catch
            {
                // Alias is not a resolvable link; fall through.
            }
        }

        return null;
    }

    private static bool IsWvdEnabled()
    {
        try
        {
            var value = SlGetWindowsInformationDword(WvdEnabledValueName, out var data);
            return value == 0 && data != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInternalBranch()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var branch = key?.GetValue("BuildBranch") as string ?? string.Empty;
            // ^(rs(?!.*release).+)  -> internal pre-release branches.
            if (!branch.StartsWith("rs", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !branch.Contains("release", StringComparison.OrdinalIgnoreCase) && branch.Length > 2;
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("slc.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SLGetWindowsInformationDWORD(string valueName, out uint value);

    private static int SlGetWindowsInformationDword(string valueName, out uint value)
        => SLGetWindowsInformationDWORD(valueName, out value);
}
