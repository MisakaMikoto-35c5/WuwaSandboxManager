namespace Wsm.Engines.Abstractions;

/// <summary>
/// Read-only access to all registered sandbox engines.
/// </summary>
public interface ISandboxEngineRegistry
{
    /// <summary>All registered engines.</summary>
    IReadOnlyList<ISandboxEngine> Engines { get; }

    /// <summary>Finds the registered engine of the given kind, or <c>null</c>.</summary>
    ISandboxEngine? Find(SandboxEngineKind kind);
}
