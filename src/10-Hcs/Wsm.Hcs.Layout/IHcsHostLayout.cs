namespace Wsm.Hcs.Layout;

/// <summary>
/// Describes the on-disk layout used by the Windows container stack and resolves
/// the base OS image layer. Everything here is <b>relocatable at the HCS level</b>,
/// even though the platform defaults to
/// <c>C:\ProgramData\Microsoft\Windows\Containers</c>.
/// </summary>
public interface IHcsHostLayout
{
    string DataRoot { get; }

    string LayersRoot { get; }

    string BaseImagesRoot { get; }

    string StorageRoot { get; }

    string SnapshotsRoot { get; }

    /// <summary>True when the process can call HCS (administrator / Hyper-V admins).</summary>
    bool IsElevated { get; }

    string GetStorageDirectory(string id);

    string GetSnapshotDirectory(string id);

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
