using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Engines.Abstractions;

namespace Wsm.Application;

/// <summary>
/// The application-level entry point for managing sandbox instances. This is
/// the interface consumed by the presentation layers; it orchestrates the
/// domain aggregate, the instance repository and the selected sandbox engine.
/// </summary>
public interface ISandboxManager
{
    /// <summary>Creates (defines) a new sandbox instance.</summary>
    Task<SandboxInstanceDto> CreateAsync(string? name, SandboxRecipe recipe, CancellationToken cancellationToken = default);

    /// <summary>Lists all known sandbox instances.</summary>
    Task<IReadOnlyList<SandboxInstanceDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts an existing instance using the given connection mode.</summary>
    Task<SandboxInstanceDto> StartAsync(Guid id, SandboxConnectionMode mode, CancellationToken cancellationToken = default);

    /// <summary>Stops an existing instance.</summary>
    Task StopAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lists the ids of running instances, optionally scoped to an engine.</summary>
    Task<IReadOnlyList<Guid>> ListRunningAsync(SandboxEngineKind? preferredEngine = null, CancellationToken cancellationToken = default);

    /// <summary>Saves a checkpoint of an instance through its selected engine.</summary>
    Task<SandboxCheckpoint> SaveCheckpointAsync(Guid id, string name, SandboxCheckpointType type, CancellationToken cancellationToken = default);

    /// <summary>Lists the checkpoints of an instance through its selected engine.</summary>
    Task<IReadOnlyList<SandboxCheckpoint>> ListCheckpointsAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Restores a checkpoint of an instance, returning the resulting VM id.</summary>
    Task<Guid> RestoreCheckpointAsync(Guid id, Guid checkpointId, CancellationToken cancellationToken = default);
}
