namespace Wsm.Hcs.Json.Container;

/// <summary>
/// The inputs needed to describe a Windows <b>process-isolated</b> container
/// compute system.
/// <para>
/// Unlike a virtual machine, a container does not boot a disk: HCS unions the
/// read-only <b>image layers</b> beneath a per-container writable <b>scratch
/// layer</b> (WCIFS). HCS requires the scratch to be an <i>activated, prepared
/// layer</i> and therefore a mounted volume, so <see cref="ScratchMountPath"/>
/// is a <c>\\?\Volume{GUID}\</c> path (from
/// <c>GetLayerMountPath</c>), not a folder.
/// </para>
/// </summary>
public sealed record ContainerDefinition
{
    /// <summary>Compute-system id (a sandbox instance id, formatted as a GUID).</summary>
    public required string Id { get; init; }

    /// <summary>
    /// The activated scratch layer's mount path (<c>\\?\Volume{GUID}\</c>) —
    /// the HCS <c>Container.Storage.Path</c>. It becomes the container's
    /// <c>C:\</c> once the read-only layers are unioned underneath it.
    /// </summary>
    public required string ScratchMountPath { get; init; }

    /// <summary>
    /// The read-only image layers, base first. Each entry maps to one
    /// <c>Container.Storage.Layers[]</c> object: <c>Id</c> is the layer GUID
    /// (<c>vmcompute!NameToGuid</c> of the path) and <c>Path</c> the layer's
    /// host directory.
    /// </summary>
    public IReadOnlyList<ContainerLayer> Layers { get; init; } = Array.Empty<ContainerLayer>();

    /// <summary>Container host name; defaults to <see cref="Id"/> when not supplied.</summary>
    public string? HostName { get; init; }

    /// <summary>HCS owner string recorded on the compute system.</summary>
    public string Owner { get; init; } = "WindowsSandboxManager";

    /// <summary>Memory limit in MB (<c>0</c> inherits the host default).</summary>
    public ulong MemoryMb { get; init; }

    /// <summary>Virtual processor count (<c>0</c> inherits the host default).</summary>
    public uint CpuCount { get; init; }

    /// <summary>
    /// The HNS namespace the container joins for networking
    /// (<c>Container.Networking.Namespace</c>). Empty means the container gets
    /// no NIC. The endpoint is added to this namespace through HNS.
    /// </summary>
    public string? NetworkNamespace { get; init; }

    /// <summary>Host folders mapped into the container at creation time.</summary>
    public IReadOnlyList<ContainerMappedFolder> MappedFolders { get; init; } =
        Array.Empty<ContainerMappedFolder>();

    public int SchemaMajor { get; init; } = 2;

    public int SchemaMinor { get; init; } = 1;
}

/// <summary>
/// One read-only image layer of a container. <paramref name="Id"/> is the layer
/// GUID HCS wants (<c>vmcompute!NameToGuid</c> of <paramref name="Path"/>) and
/// <paramref name="Path"/> is the layer's host directory (the folder that holds
/// <c>Files</c> and <c>Hives</c>).
/// </summary>
public sealed record ContainerLayer(string Id, string Path);
