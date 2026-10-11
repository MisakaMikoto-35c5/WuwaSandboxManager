namespace Wsm.Hcs.Storage;

/// <summary>Resolved storage paths for a sandbox.</summary>
/// <param name="Directory">The sandbox's storage directory.</param>
/// <param name="DiskPath">The writable user-data disk (<c>sandbox.vhdx</c>).</param>
/// <param name="GuestStatePath">The guest-state file (<c>sandbox.vmgs</c>).</param>
/// <param name="BaseDiskPath">
/// The parent of the user-data disk when it is a differencing child. By default
/// this is the platform's own base OS disk, <b>referenced in place</b> (it is
/// never copied); it may be <see langword="null"/> for a standalone/blank disk.
/// It is only inside the sandbox directory when the user explicitly configured a
/// base image.
/// </param>
public sealed record StorageLayout(
    string Directory,
    string DiskPath,
    string GuestStatePath,
    string? BaseDiskPath = null);
