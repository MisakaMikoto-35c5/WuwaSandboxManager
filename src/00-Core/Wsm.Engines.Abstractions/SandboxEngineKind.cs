namespace Wsm.Engines.Abstractions;

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

    /// <summary>Windows container (lightweight isolation) engine. Reserved, not yet implemented.</summary>
    WindowsContainer = 2,
}
