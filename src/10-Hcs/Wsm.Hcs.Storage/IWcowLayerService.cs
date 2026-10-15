namespace Wsm.Hcs.Storage;

/// <summary>
/// The WCOW (Windows container) layer APIs exported by <c>vmcompute.dll</c>,
/// which the Windows container manager itself uses.
/// <para>
/// A process-isolated container's root filesystem is assembled from read-only
/// image layers plus a per-container writable <b>scratch layer</b>. Creating one
/// is a fixed sequence, and HCS rejects a container whose scratch is not a
/// mounted, prepared layer (<c>0x80071126</c> "not a reparse point" /
/// <c>0x80070057</c> "invalid argument"):
/// </para>
/// <list type="number">
///   <item><see cref="CreateScratchLayer"/> builds the scratch (and its
///   <c>sandbox.vhdx</c>) on top of the read-only layers;</item>
///   <item><see cref="Activate"/> mounts the scratch as a host volume;</item>
///   <item><see cref="Prepare"/> attaches the WCIFS overlay that unions the
///   read-only layers beneath the scratch volume;</item>
///   <item><see cref="GetMountPath"/> returns the <c>\\?\Volume{GUID}\</c> path
///   HCS expects as <c>Container.Storage.Path</c>;</item>
///   <item>HCS's <c>Storage.Layers[].Id</c> is <see cref="GetLayerId"/> of each
///   read-only layer, and <c>Storage.Layers[].Path</c> its host directory.</item>
/// </list>
/// <para>
/// The APIs are undocumented and every call requires an elevated process (an
/// administrator or a Hyper-V administrator).
/// </para>
/// </summary>
public interface IWcowLayerService
{
    /// <summary>
    /// The deterministic layer GUID HCS wants for a layer path
    /// (<c>vmcompute!NameToGuid</c>). It is a stable hash of the path, so every
    /// process computes the same id.
    /// </summary>
    Guid GetLayerId(string layerPath);

    /// <summary>
    /// Creates a new writable scratch layer at <paramref name="scratchPath"/>
    /// (its <c>sandbox.vhdx</c> is created there if missing) whose parents are
    /// <paramref name="parentLayerPaths"/> (base first, top-most last).
    /// </summary>
    void CreateScratchLayer(string scratchPath, IReadOnlyList<string> parentLayerPaths);

    /// <summary>Mounts an existing layer (read-write layers appear as volumes).</summary>
    void Activate(string layerPath);

    /// <summary>
    /// Attaches the WCIFS overlay to the (activated) scratch layer so the
    /// read-only parents are unioned beneath it. Must be paired with
    /// <see cref="Unprepare"/>.
    /// </summary>
    void Prepare(string layerPath, IReadOnlyList<string> parentLayerPaths);

    /// <summary>Detaches the overlay attached by <see cref="Prepare"/>.</summary>
    void Unprepare(string layerPath);

    /// <summary>Unmounts a layer mounted by <see cref="Activate"/>.</summary>
    void Deactivate(string layerPath);

    /// <summary>
    /// The path at which an activated layer is accessible: the layer directory
    /// for a read-only layer, or a <c>\\?\Volume{GUID}\</c> volume for an
    /// activated scratch layer.
    /// </summary>
    string GetMountPath(string layerPath);

    /// <summary>Destroys a layer (removes its scratch files). Best effort.</summary>
    void Destroy(string layerPath);

    /// <summary>
    /// Imports a layer from an exported folder layout
    /// (<c>&lt;importFolder&gt;\Files</c> + <c>\Hives</c>) into a new layer at
    /// <paramref name="layerPath"/>. Used to bring a downloaded base image in.
    /// </summary>
    void ImportLayer(string layerPath, string importFolderPath, IReadOnlyList<string> parentLayerPaths);

    /// <summary>
    /// Post-processes an extracted <b>base</b> image layer
    /// (<c>&lt;layer&gt;\Files</c>): fixes up reparse points and prepares the
    /// registry hives so HCS can consume the layer.
    /// </summary>
    void ProcessBaseImage(string layerPath);
}
