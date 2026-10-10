namespace WindowsSandboxManager.Domain.Abstractions;

/// <summary>
/// Describes what a concrete backend can actually do. The domain/application
/// layers use this to decide whether a requested capability (e.g. CPU pinning,
/// custom storage location or checkpoints) is achievable, instead of failing late.
/// </summary>
public sealed record SandboxBackendCapabilities
{
    /// <summary>Whether more than one sandbox can run at the same time.</summary>
    public bool SupportsMultipleInstances { get; init; }

    /// <summary>Whether virtual processor count can be controlled.</summary>
    public bool SupportsCpuConfiguration { get; init; }

    /// <summary>Whether memory can be changed on a live instance.</summary>
    public bool SupportsLiveMemoryResize { get; init; }

    /// <summary>Whether folders can be mapped into a running instance.</summary>
    public bool SupportsRuntimeFolderMapping { get; init; }

    /// <summary>Whether arbitrary processes can be executed inside the guest.</summary>
    public bool SupportsProcessExecution { get; init; }

    /// <summary>Whether a running instance can be enumerated.</summary>
    public bool SupportsEnumeration { get; init; }

    /// <summary>Whether reboot-without-recreate is available.</summary>
    public bool SupportsReboot { get; init; }

    /// <summary>Whether save/restore checkpoints are supported.</summary>
    public bool SupportsCheckpoints { get; init; }

    /// <summary>Whether the user-data disk location can be customised.</summary>
    public bool SupportsCustomStorageLocation { get; init; }

    /// <summary>Whether the base OS image layer location can be customised.</summary>
    public bool SupportsCustomBaseImage { get; init; }

    /// <summary>Whether the backend needs an elevated (administrator) process.</summary>
    public bool RequiresElevation { get; init; }

    public static SandboxBackendCapabilities None { get; } = new();
}
