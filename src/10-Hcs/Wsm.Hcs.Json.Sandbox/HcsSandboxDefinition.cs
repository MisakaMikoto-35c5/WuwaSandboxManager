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

    /// <summary>
    /// Host path for the guest crash dump (<c>GuestCrashReporting.WindowsCrashSettings.DumpFileName</c>).
    /// Its directory must already exist; when null the guest crash reporting
    /// device is omitted entirely (HCS otherwise fails Start with
    /// <c>0x80070003</c> because its default dump path is not present).
    /// </summary>
    public string? DumpFilePath { get; init; }

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

    /// <summary>
    /// Extra HvSocket services to expose (beyond the built-in LSM/NLM pair). The
    /// Windows Sandbox configuration carries a fixed set of these; the guest's
    /// interactive session infrastructure is wired to them, so a configuration
    /// with only LSM/NLM leaves the session unable to complete its logon.
    /// </summary>
    public IReadOnlyList<HcsHvSocketService> AdditionalHvSocketServices { get; init; } = Array.Empty<HcsHvSocketService>();

    public int SchemaMajor { get; init; } = 2;

    public int SchemaMinor { get; init; } = 1;
}
