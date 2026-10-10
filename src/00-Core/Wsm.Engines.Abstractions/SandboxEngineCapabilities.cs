namespace Wsm.Engines.Abstractions;

/// <summary>
/// The optional capabilities an engine may support. Consumers must gate calls
/// on these flags rather than assuming a capability exists.
/// </summary>
public sealed record SandboxEngineCapabilities
{
    /// <summary>Whether more than one instance can run at the same time.</summary>
    public bool SupportsMultipleInstances { get; init; }

    /// <summary>Whether the CPU (core count) can be configured per instance.</summary>
    public bool SupportsCpuConfiguration { get; init; }

    /// <summary>Whether the user-data disk location can be customized.</summary>
    public bool SupportsCustomStorageLocation { get; init; }

    /// <summary>Whether a custom base image can be supplied.</summary>
    public bool SupportsCustomBaseImage { get; init; }

    /// <summary>Whether save/restore checkpoints are supported.</summary>
    public bool SupportsCheckpoints { get; init; }

    /// <summary>Whether host folders can be mapped while the instance is running.</summary>
    public bool SupportsRuntimeFolderMapping { get; init; }

    /// <summary>Whether processes can be executed inside the guest.</summary>
    public bool SupportsProcessExecution { get; init; }

    /// <summary>Whether running instances can be enumerated.</summary>
    public bool SupportsEnumeration { get; init; }

    /// <summary>Whether the engine requires the host process to be elevated.</summary>
    public bool RequiresElevation { get; init; }

    /// <summary>Capabilities that claim nothing is supported.</summary>
    public static SandboxEngineCapabilities None => new();
}
