using Wsm.Engines.Abstractions;

namespace Wsm.Application.EngineSelection;

/// <summary>
/// Chooses the sandbox engine that should serve a given start request.
/// </summary>
public interface ISandboxEngineSelector
{
    /// <summary>
    /// Selects an engine for the request. Prefers an explicitly requested engine
    /// when it is available, then a capability match, then the lowest
    /// <see cref="SandboxEngineKind"/> (HCS first).
    /// </summary>
    ISandboxEngine Select(SandboxStartRequest request);
}
