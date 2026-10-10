namespace Wsm.Hcs.Json.Sandbox;

/// <summary>
/// Everything the HCS engine needs to build a Windows Sandbox compute system.
/// All paths are explicit, which is how custom base-image and user-data
/// locations are supported (the platform's own stack hardcodes them).
/// </summary>
public sealed record HcsSandboxDefinition
{
    public required string Id { get; init; }

    /// <summary>Base OS image layer files root (<c>...\Layers\&lt;id&gt;\Files</c>).</summary>
    public required string BaseLayerPath { get; init; }

    /// <summary>User-data VHDX path (custom storage location).</summary>
    public required string StorageDiskPath { get; init; }

    /// <summary>Guest state file path (<c>sandbox.vmgs</c>).</summary>
    public required string GuestStatePath { get; init; }

    public string Owner { get; init; } = "WindowsSandbox";

    public uint MemoryMb { get; init; } = 4096;

    public int CpuCount { get; init; } = 4;

    public bool EnableVirtualGpu { get; init; } = true;

    public bool EnableNetworking { get; init; }

    /// <summary>HNS endpoint id; required for the guest to actually get a NIC.</summary>
    public string? NetworkEndpointId { get; init; }

    public IReadOnlyList<HcsMappedFolder> MappedFolders { get; init; } = Array.Empty<HcsMappedFolder>();

    /// <summary><c>RestoreState.TemplateSystemId</c> used to clone from a template.</summary>
    public string? TemplateSystemId { get; init; }

    /// <summary><c>RestoreState.SaveStateFilePath</c> used to resume a full-state checkpoint.</summary>
    public string? RestoreStateFilePath { get; init; }

    /// <summary>Named pipe for the EnhancedModeVideo (RDP) endpoint.</summary>
    public string? RdpNamedPipe { get; init; }

    public IReadOnlyList<string> RdpAccessSids { get; init; } = Array.Empty<string>();

    /// <summary>Extra HvSocket service GUIDs to expose (beyond LSM/NLM).</summary>
    public IReadOnlyList<Guid> AdditionalHvSocketServices { get; init; } = Array.Empty<Guid>();

    public int SchemaMajor { get; init; } = 2;

    public int SchemaMinor { get; init; } = 1;
}
