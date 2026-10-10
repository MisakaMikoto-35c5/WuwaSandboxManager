namespace Wsm.Hcs.Layout;

/// <summary>
/// Describes the on-disk layout used by the Windows container stack and resolves
/// the base OS image layer. The platform containers tree
/// (<c>C:\ProgramData\Microsoft\Windows\Containers</c>) holds the read-only
/// <c>Layers</c>/<c>BaseImages</c> stores and must stay there; each sandbox's own
/// config and data lives under the user-selectable
/// <see cref="SandboxDataFolder"/>.
/// </summary>
public interface IHcsHostLayout
{
    /// <summary>The platform containers folder (<c>C:\ProgramData\Microsoft\Windows\Containers</c>).</summary>
    string PlatformContainersFolder { get; }

    /// <summary>The registry-backed layer store (<c>...\Containers\Layers</c>).</summary>
    string LayersFolder { get; }

    /// <summary>The platform base-image store (<c>...\Containers\BaseImages</c>).</summary>
    string BaseImagesFolder { get; }

    /// <summary>
    /// The platform container-storage folder (<c>...\Containers\ContainerStorages</c>),
    /// used as the donor source when seeding a sandbox's user-data disk.
    /// </summary>
    string PlatformContainerStoragesFolder { get; }

    /// <summary>The platform snapshots folder (<c>...\Containers\Snapshots</c>).</summary>
    string SnapshotsFolder { get; }

    /// <summary>
    /// The config &amp; data folder that holds one subfolder per sandbox (its
    /// manifest, recipe, instance record and user-data disks live there).
    /// User-selectable; defaults to <c>%USERPROFILE%\Documents\WsmContainers</c>.
    /// </summary>
    string SandboxDataFolder { get; }

    /// <summary>The per-sandbox folder (<see cref="SandboxDataFolder"/>\<paramref name="id"/>).</summary>
    string GetStorageDirectory(string id);

    /// <summary>Enumerates the registered layers (newest sequence number first).</summary>
    IReadOnlyList<HcsLayerInfo> ListLayers();

    /// <summary>
    /// Resolves the most recent usable base OS image layer (the layer whose
    /// <c>Files</c> folder contains a <c>Windows</c> directory). This is what the
    /// sandbox exposes to the guest through <c>VirtualSmb.Shares[0]</c> and
    /// <c>RunInSilo.SiloBaseOsPath</c>.
    /// </summary>
    string? ResolveBaseLayerPath();
}
