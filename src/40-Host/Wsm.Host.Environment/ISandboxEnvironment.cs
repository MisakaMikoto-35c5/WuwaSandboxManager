namespace Wsm.Host.Environment;

/// <summary>
/// Describes the host Windows Sandbox installation (feature availability,
/// package path, control-plane pipe name and executables).
/// </summary>
public interface ISandboxEnvironment
{
    /// <summary>True when the Containers-DisposableClientVM feature is present.</summary>
    bool IsSupported { get; }

    /// <summary>True when the current process is elevated (required by the HCS engine).</summary>
    bool IsElevated { get; }

    /// <summary>
    /// True when the host policy allows more than one sandbox at a time
    /// (WDAG/AVD hosts or internal builds).
    /// </summary>
    bool IsMultipleInstancesAllowed { get; }

    /// <summary>Path to the installed "Windows Sandbox" MSIX package, if any.</summary>
    string? PackageInstallPath { get; }

    /// <summary>Named pipe the control-plane gRPC server listens on.</summary>
    string? ServerPipeName { get; }

    /// <summary>Legacy full-desktop client (<c>WindowsSandbox.exe</c>).</summary>
    string? LegacyExecutablePath { get; }

    /// <summary>
    /// Modern per-instance RDP client, resolved across candidate names and
    /// locations: <c>WindowsSandboxRemoteSession.exe</c> and
    /// <c>WindowsSandboxClient.exe</c>, in the packaged AppExecutionAlias
    /// directory (<c>%LocalAppData%\Microsoft\WindowsApps</c>), the package
    /// install directory and <c>System32</c>. The first existing candidate wins.
    /// </summary>
    string? RemoteSessionExecutablePath { get; }

    /// <summary>
    /// The platform command-line interface (<c>wsb.exe</c>), resolved through its
    /// AppExecutionAlias in <c>%LocalAppData%\Microsoft\WindowsApps</c>. Its
    /// <c>connect --id &lt;id&gt;</c> subcommand opens a remote session for a
    /// sandbox known to <c>WindowsSandboxServer</c> and is the preferred way to
    /// reconnect to a running instance.
    /// </summary>
    string? SandboxCliExecutablePath { get; }

    /// <summary>Control-plane server executable (<c>WindowsSandboxServer.exe</c>).</summary>
    string? ServerExecutablePath { get; }

    /// <summary>Starts the control-plane server if it is not already running.</summary>
    bool EnsureServerRunning();
}
