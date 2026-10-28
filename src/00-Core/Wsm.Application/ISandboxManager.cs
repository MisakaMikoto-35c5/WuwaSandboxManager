using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;

namespace Wsm.Application;

/// <summary>
/// The application-level entry point for managing sandbox instances. This is
/// the interface consumed by the presentation layers; it orchestrates the
/// domain aggregate, the instance repository and the selected sandbox engine.
/// </summary>
public interface ISandboxManager
{
    /// <summary>Creates (defines) a new sandbox instance, optionally pinned to an engine.</summary>
    Task<SandboxInstanceDto> CreateAsync(string? name, SandboxRecipe recipe, SandboxEngineKind? engine = null, CancellationToken cancellationToken = default);

    /// <summary>Lists all known sandbox instances.</summary>
    Task<IReadOnlyList<SandboxInstanceDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a portable <c>.wsm</c> from an arbitrary path (association / open
    /// dialog / cli argument), resolving its relative paths against the file, so
    /// it can be started with zero configuration.
    /// </summary>
    Task<SandboxInstanceDto> ImportAsync(string manifestPath, CancellationToken cancellationToken = default);

    /// <summary>Gets a single sandbox instance, throwing when it does not exist.</summary>
    Task<SandboxInstanceDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Deletes a sandbox instance, stopping it first when it is live.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Returns the live runtime of a running instance, resolving it through the
    /// selected engine when the aggregate does not carry one. Returns <c>null</c>
    /// when the instance exists but its engine reports it is not running; an
    /// unknown id throws <see cref="Wsm.Domain.Exceptions.SandboxNotFoundException"/>.
    /// </summary>
    Task<SandboxRuntimeInfo?> GetRuntimeAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Server-side pre-connect preparation: resolves the instance's live runtime
    /// (engine-backed) and reclaims any orphaned guest sessions, returning the
    /// runtime so the caller can establish the session itself. Throws
    /// <see cref="Wsm.Domain.Exceptions.SandboxDomainException"/> when the instance
    /// is not running and <see cref="Wsm.Domain.Exceptions.SandboxNotFoundException"/>
    /// for an unknown id.
    /// </summary>
    Task<SandboxRuntimeInfo> PrepareConnectAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Opens the user-visible session for a running instance.</summary>
    Task ConnectAsync(Guid id, SandboxConnectionMode mode, string? remoteAppProgram = null, string? remoteAppArguments = null, CancellationToken cancellationToken = default);

    /// <summary>Returns the guest IP addresses of an instance.</summary>
    Task<IReadOnlyList<string>> GetIpAddressesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Maps a host folder into a running instance and records it on the definition.</summary>
    Task MapFolderAsync(Guid id, MappedFolder folder, CancellationToken cancellationToken = default);

    /// <summary>Executes a command inside an instance and returns its exit code.</summary>
    Task<int> ExecuteAsync(Guid id, string command, CancellationToken cancellationToken = default);

    /// <summary>Reports the capabilities of the engine that would serve a default recipe.</summary>
    Task<SandboxCapabilitiesDto> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
}
