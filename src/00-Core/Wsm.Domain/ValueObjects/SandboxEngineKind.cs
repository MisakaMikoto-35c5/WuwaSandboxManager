namespace Wsm.Domain.ValueObjects;

/// <summary>
/// Identifies a concrete sandbox engine implementation. Registration order of
/// the values doubles as the default selection priority (HCS first).
/// </summary>
public enum SandboxEngineKind
{
    /// <summary>HCS (Host Compute Service) VM-based sandbox. Requires elevation.</summary>
    HcsSandbox = 0,

    /// <summary>Non-elevated Windows Sandbox reached over the named-pipe gRPC server.</summary>
    WindowsSandbox = 1,

    /// <summary>
    /// Windows container (process-isolated, lightweight isolation) engine.
    /// Reuses the Windows Sandbox minimal image layer and requires elevation.
    /// </summary>
    WindowsContainer = 2,
}
