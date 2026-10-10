using Wsm.Domain.ValueObjects;

namespace Wsm.Engines.Abstractions;

/// <summary>
/// A request to start a sandbox instance, optionally constraining which engine
/// may serve it.
/// </summary>
public sealed record SandboxStartRequest
{
    /// <summary>
    /// The identity the engine must use when assembling the compute system,
    /// storage directory and RDP pipe. The engine must guarantee
    /// <c>runtime.SandboxId == Id</c>.
    /// </summary>
    public required SandboxInstanceId Id { get; init; }

    /// <summary>The complete configuration of the requested sandbox.</summary>
    public required SandboxRecipe Recipe { get; init; }

    /// <summary>How the user wants to consume the sandbox.</summary>
    public SandboxConnectionMode ConnectionMode { get; init; } = SandboxConnectionMode.FullDesktop;

    /// <summary>Optional engine kind to prefer.</summary>
    public SandboxEngineKind? PreferredEngine { get; init; }
}
