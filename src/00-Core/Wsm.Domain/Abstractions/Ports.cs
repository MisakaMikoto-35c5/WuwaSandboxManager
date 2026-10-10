using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;

namespace WindowsSandboxManager.Domain.Abstractions;

/// <summary>
/// Driven port: a control plane capable of creating and driving Windows Sandbox
/// instances. Concrete adapters include the gRPC <c>WindowsSandboxServer</c>
/// backend and the low level HCS backend.
/// </summary>
public interface ISandboxBackend
{
    string Name { get; }

    /// <summary>Whether the backend can be used right now (e.g. HCS needs elevation).</summary>
    bool IsAvailable => true;

    SandboxBackendCapabilities Capabilities { get; }

    Task<IReadOnlyList<SandboxInstanceId>> EnumerateRunningAsync(CancellationToken cancellationToken = default);

    Task<SandboxRuntimeInfo> StartAsync(
        SandboxRecipe recipe,
        SandboxInstanceId id,
        CancellationToken cancellationToken = default);

    Task ShutdownAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    Task MapFolderAsync(SandboxInstanceId id, MappedFolder folder, CancellationToken cancellationToken = default);

    Task<int> ExecuteAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    Task<SandboxRuntimeInfo?> GetRuntimeInfoAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    Task<SandboxTerminationReason> WaitForTerminationAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a checkpoint of a running instance. Only supported when
    /// <see cref="SandboxBackendCapabilities.SupportsCheckpoints"/> is true.
    /// </summary>
    Task<SandboxCheckpoint> SaveCheckpointAsync(
        SandboxInstanceId id,
        string name,
        SandboxCheckpointType type,
        CancellationToken cancellationToken = default)
        => throw new SandboxNotSupportedException($"Backend '{Name}' does not support checkpoints.");

    /// <summary>Lists checkpoints known for an instance.</summary>
    Task<IReadOnlyList<SandboxCheckpoint>> ListCheckpointsAsync(
        SandboxInstanceId id,
        CancellationToken cancellationToken = default)
        => throw new SandboxNotSupportedException($"Backend '{Name}' does not support checkpoints.");

    /// <summary>Restores a checkpoint, returning the (possibly new) system id.</summary>
    Task<Guid> RestoreCheckpointAsync(
        SandboxInstanceId id,
        SandboxCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
        => throw new SandboxNotSupportedException($"Backend '{Name}' does not support checkpoints.");
}

/// <summary>
/// Driven port: opens a user visible RDP session to a running sandbox, either
/// as a full desktop or as RemoteApp/RAIL windows.
/// </summary>
public interface ISandboxSessionConnector
{
    Task ConnectAsync(
        SandboxInstanceId id,
        SandboxRecipe recipe,
        SandboxRuntimeInfo runtime,
        SandboxConnectionMode mode,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Driven port: converts a <see cref="SandboxRecipe"/> to/from the on-the-wire
/// representation (the .wsb XML understood by Windows Sandbox).
/// </summary>
public interface ISandboxRecipeSerializer
{
    string Serialize(SandboxRecipe recipe);

    SandboxRecipe Deserialize(string payload);
}

/// <summary>Driven port: persistence for sandbox instance definitions.</summary>
public interface ISandboxRepository
{
    Task SaveAsync(SandboxInstance instance, CancellationToken cancellationToken = default);

    Task<SandboxInstance?> GetAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SandboxInstance>> ListAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Driven port: describes the host Windows Sandbox installation (feature
/// availability, package path, control-plane pipe name and executables).
/// </summary>
public interface ISandboxEnvironment
{
    /// <summary>True when the Containers-DisposableClientVM feature is present.</summary>
    bool IsSupported { get; }

    /// <summary>True when the current process is elevated (required by the HCS backend).</summary>
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
