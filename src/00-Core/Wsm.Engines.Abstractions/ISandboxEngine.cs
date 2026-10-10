using WindowsSandboxManager.Domain.ValueObjects;

namespace Wsm.Engines.Abstractions;

/// <summary>
/// The single contract every sandbox engine implements. Optional capabilities
/// are declared via <see cref="Capabilities"/> and gated by the caller; the
/// optional methods are declared here as plain members (a default-throwing
/// behaviour is layered on later).
/// </summary>
public interface ISandboxEngine
{
    /// <summary>Human-readable engine name.</summary>
    string Name { get; }

    /// <summary>Which engine this is.</summary>
    SandboxEngineKind Kind { get; }

    /// <summary>The capabilities this engine supports.</summary>
    SandboxEngineCapabilities Capabilities { get; }

    /// <summary>Whether the engine can currently be used (e.g. HCS needs elevation).</summary>
    bool IsAvailable { get; }

    /// <summary>Enumerates the instances currently known to this engine.</summary>
    Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts a new instance and returns its live runtime information.</summary>
    Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stops the given instance.</summary>
    Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    /// <summary>Gets live runtime information, or <c>null</c> when not running.</summary>
    Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    /// <summary>Maps a host folder into a running instance.</summary>
    Task MapFolderAsync(SandboxInstanceId id, MappedFolder folder, CancellationToken cancellationToken = default);

    /// <summary>Executes a command inside the instance and returns its exit code.</summary>
    Task<int> ExecuteAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default);

    /// <summary>Returns the guest IP addresses.</summary>
    Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    /// <summary>Saves a checkpoint of the instance.</summary>
    Task<SandboxCheckpoint> SaveCheckpointAsync(SandboxInstanceId id, string name, SandboxCheckpointType type, CancellationToken cancellationToken = default);

    /// <summary>Restores the instance from a checkpoint and returns the resulting VM id.</summary>
    Task<Guid> RestoreCheckpointAsync(SandboxInstanceId id, SandboxCheckpoint checkpoint, CancellationToken cancellationToken = default);
}
