namespace Wsm.Hcs.Storage;

/// <summary>Resolved storage paths for a sandbox.</summary>
/// <param name="Directory">The sandbox's storage directory.</param>
/// <param name="DiskPath">The writable user-data disk (<c>sandbox.vhdx</c>).</param>
/// <param name="GuestStatePath">The persistent guest-state file (<c>sandbox.vmgs</c>).</param>
/// <param name="BaseDiskPath">
/// The uncompressed base-OS copy (<c>sandbox.base.vhdx</c>) that a differencing
/// <see cref="DiskPath"/> is built over. The path is always resolved; the file
/// only exists when the differencing-chain strategy produced the disk, so the
/// caller must check for its presence before using it.
/// </param>
public sealed record StorageLayout(
    string Directory,
    string DiskPath,
    string GuestStatePath,
    string BaseDiskPath);
