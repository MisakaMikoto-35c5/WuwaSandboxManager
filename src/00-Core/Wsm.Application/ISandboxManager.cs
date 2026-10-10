using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Engines.Abstractions;

namespace Wsm.Application;

/// <summary>
/// The application-level entry point for managing sandbox instances. This is
/// the interface consumed by the presentation layers; its implementation lands
/// in a later phase.
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
}
