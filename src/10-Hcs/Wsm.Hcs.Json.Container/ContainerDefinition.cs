namespace Wsm.Hcs.Json.Container;

/// <summary>
/// The inputs needed to describe a Windows <b>container</b> compute system.
/// <para>
/// A container is the lightweight (process-isolated) sibling of the Windows
/// Sandbox virtual machine: it <b>shares the same minimal OS image layer</b> that
/// the Windows Sandbox provisions (<see cref="BaseLayerPath"/>) but exposes it to
/// HCS as a read-only container layer instead of a bootable VM disk.
/// </para>
/// </summary>
public sealed record ContainerDefinition
{
    /// <summary>Compute-system id (a sandbox instance id, formatted as a GUID).</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The read-only base OS image layer (<c>...\Layers\&lt;id&gt;\Files</c>) that
    /// the container shares with the Windows Sandbox. Resolved through
    /// <c>IHcsHostLayout.ResolveBaseLayerPath()</c>.
    /// </summary>
    public required string BaseLayerPath { get; init; }

    /// <summary>
    /// The layer id of <see cref="BaseLayerPath"/> when it is known (the layer
    /// GUID segment of the path). HCS expects a unique id per layer, so it is
    /// emitted alongside the path when available.
    /// </summary>
    public string? BaseLayerId { get; init; }

    /// <summary>
    /// The container's writable scratch (<c>Container.Storage.Path</c>).
    /// <para>
    /// HCS's process-isolated (Argon) runtime expects this to be a mounted,
    /// prepared layer volume (a <c>\\?\Volume{...}\</c> path produced by the
    /// vmcompute layer APIs), not an arbitrary directory. Provisioning that
    /// volume is not implemented yet; the engine passes a per-instance scratch
    /// directory as a best-effort fallback and lets HCS report the mismatch.
    /// </para>
    /// </summary>
    public required string ScratchPath { get; init; }

    /// <summary>Container host name; defaults to <see cref="Id"/> when not supplied.</summary>
    public string? HostName { get; init; }

    /// <summary>HCS owner string recorded on the compute system.</summary>
    public string Owner { get; init; } = "WindowsSandboxManager";

    /// <summary>Memory limit in MB (<c>0</c> inherits the host default).</summary>
    public ulong MemoryMb { get; init; }

    /// <summary>Virtual processor count (<c>0</c> inherits the host default).</summary>
    public uint CpuCount { get; init; }

    /// <summary>
    /// Whether to attach the network adapters listed in
    /// <see cref="NetworkAdapterIds"/>. When false the container runs without a
    /// NIC, which is the safe default while no HNS network is configured.
    /// </summary>
    public bool EnableNetworking { get; init; }

    /// <summary>HNS endpoint ids (GUIDs) to attach; ignored when networking is disabled.</summary>
    public IReadOnlyList<string> NetworkAdapterIds { get; init; } = Array.Empty<string>();

    /// <summary>Host folders mapped into the container at creation time.</summary>
    public IReadOnlyList<ContainerMappedFolder> MappedFolders { get; init; } =
        Array.Empty<ContainerMappedFolder>();

    public int SchemaMajor { get; init; } = 2;

    public int SchemaMinor { get; init; } = 1;
}
