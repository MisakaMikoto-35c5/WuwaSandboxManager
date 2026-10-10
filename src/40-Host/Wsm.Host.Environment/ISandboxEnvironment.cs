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

    /// <summary>Modern per-instance RDP client (<c>WindowsSandboxRemoteSession.exe</c>).</summary>
    string? RemoteSessionExecutablePath { get; }

    /// <summary>Control-plane server executable (<c>WindowsSandboxServer.exe</c>).</summary>
    string? ServerExecutablePath { get; }

    /// <summary>Starts the control-plane server if it is not already running.</summary>
    bool EnsureServerRunning();
}
