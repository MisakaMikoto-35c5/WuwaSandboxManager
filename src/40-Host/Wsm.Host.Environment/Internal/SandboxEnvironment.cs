using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Volo.Abp.DependencyInjection;
using Wsm.Host.Elevation;
using Wsm.Host.Policy;

namespace Wsm.Host.Environment.Internal;

/// <summary>
/// Default <see cref="ISandboxEnvironment"/>: reproduces the detection logic used
/// by Microsoft's own <c>SandboxContracts</c> / <c>GrpcServer</c> so that we agree
/// with the platform about availability, multi-instance policy and the
/// control-plane pipe name. The multi-instance policy itself is delegated to
/// <see cref="IMultiInstancePolicy"/>.
/// </summary>
[ExposeServices(typeof(ISandboxEnvironment))]
internal sealed class SandboxEnvironment : ISandboxEnvironment, ISingletonDependency
{
    private const string SystemDirectoryRelativeExe = "WindowsSandbox.exe";
    private const string RemoteSessionAlias = "WindowsSandboxRemoteSession.exe";
    private const string ServerAlias = "WindowsSandboxServer.exe";
    private const string ClientAlias = "WindowsSandboxClient.exe";
    private const string SandboxCliAlias = "wsb.exe";

    private readonly IMultiInstancePolicy _policy;
    private readonly IElevationService _elevation;
    private readonly Lazy<string?> _packagePath;
    private readonly Lazy<bool> _isSupported;

    public SandboxEnvironment(IMultiInstancePolicy policy, IElevationService elevation)
    {
        _policy = policy;
        _elevation = elevation;
        _packagePath = new Lazy<string?>(ResolvePackagePath);
        _isSupported = new Lazy<bool>(ResolveIsSupported);
    }

    public bool IsSupported => _isSupported.Value;

    public bool IsElevated => _elevation.IsElevated;

    public bool IsMultipleInstancesAllowed => _policy.AllowsMultipleInstances;

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

    public string? RemoteSessionExecutablePath => ResolveModernClient();

    public string? SandboxCliExecutablePath => ResolveAlias(SandboxCliAlias);

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

    private string? ResolveModernClient()
    {
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var windowsApps = Path.Combine(local, "Microsoft", "WindowsApps");
        return SandboxClientResolver.Resolve(
            PackageInstallPath,
            windowsApps,
            System.Environment.SystemDirectory,
            File.Exists);
    }

    private static string? ResolvePackagePath()
    {
        // Enumerating C:\Program Files\WindowsApps is denied for unelevated
        // processes, so resolve the package through its AppExecutionAlias
        // reparse point instead. The alias is an IO_REPARSE_TAG_APPEXECLINK
        // (not a symlink), so read the reparse data directly.
        foreach (var alias in new[] { ServerAlias, RemoteSessionAlias, ClientAlias })
        {
            var aliasPath = ResolveAlias(alias);
            if (aliasPath is null)
            {
                continue;
            }

            var target = AppExecLink.TryGetTarget(aliasPath);
            if (target is null)
            {
                try
                {
                    var link = File.ResolveLinkTarget(aliasPath, returnFinalTarget: true);
                    target = link is FileInfo file ? file.FullName : link?.FullName;
                }
                catch
                {
                    // Alias is not a resolvable link; fall through.
                }
            }

            if (target is null)
            {
                continue;
            }

            var current = Path.GetDirectoryName(target);
            for (var i = 0; i < 8 && current is not null; i++)
            {
                if (File.Exists(Path.Combine(current, "AppxManifest.xml")))
                {
                    return current;
                }

                current = Path.GetDirectoryName(current);
            }
        }

        return null;
    }
}
