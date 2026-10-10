namespace Wsm.Hcs.Storage;

/// <summary>
/// Creates and inspects virtual disks for a sandbox's user-data storage.
/// Abstracted so the provisioning logic can be unit tested without touching the
/// Win32 <c>VirtDisk</c> API (which needs an elevated process).
/// </summary>
public interface IVirtualDiskFactory
{
    /// <summary>
    /// Creates a new <b>blank dynamic VHDX</b> at <paramref name="path"/>.
    /// <para>
    /// Windows Sandbox's own freshly-provisioned <c>sandbox.vhdx</c> is likewise
    /// an empty dynamic VHDX (its on-disk size is only a few MB and its data
    /// region is entirely zero — there is no pre-formatted filesystem; the
    /// guest's container runtime lays the writable overlay down at boot).
    /// </para>
    /// </summary>
    /// <param name="path">Absolute path of the <c>.vhdx</c> to create.</param>
    /// <param name="maximumSizeBytes">
    /// Maximum (grown-to) size in bytes. <c>0</c> selects the platform default.
    /// </param>
    void CreateDynamicVhdx(string path, ulong maximumSizeBytes);

    /// <summary>
    /// Creates a new <b>differencing VHDX</b> at <paramref name="path"/> whose
    /// parent is the existing disk at <paramref name="parentPath"/>.
    /// <para>
    /// The child is a few MB and inherits the parent's geometry and contents; it
    /// is the shape HCS's synthetic storage expects for a sandbox user-data disk
    /// (a differencing child over the base OS disk). The parent must already
    /// exist and be an uncompressed VHDX — a compressed disk anywhere in the
    /// chain makes HCS fail <c>Start</c> with <c>0xC03A001A</c>.
    /// </para>
    /// </summary>
    /// <param name="path">Absolute path of the differencing <c>.vhdx</c> to create.</param>
    /// <param name="parentPath">Absolute path of the existing parent <c>.vhdx</c>.</param>
    void CreateDifferencingVhdx(string path, string parentPath);

    /// <summary>
    /// Returns the absolute path of <paramref name="vhdxPath"/>'s parent when it
    /// is a differencing disk, or <see langword="null"/> when it is a base disk
    /// (no parent locator) or cannot be parsed.
    /// </summary>
    string? TryGetParentPath(string vhdxPath);
}
