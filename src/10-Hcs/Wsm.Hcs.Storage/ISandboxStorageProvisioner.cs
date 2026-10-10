namespace Wsm.Hcs.Storage;

/// <summary>
/// Provisions the user-data storage for a sandbox at a chosen location. Windows
/// Sandbox's own stack always writes to
/// <c>...\ContainerStorages\&lt;id&gt;\sandbox.vhdx</c>; this makes the
/// <b>location</b> configurable by staging the disk/guest-state files wherever
/// the caller asks.
/// <para>
/// Provisioning is best-effort in this order: reuse an existing usable file;
/// build an <b>uncompressed differencing chain</b> from the platform base OS
/// disk (copy the base uncompressed, then create <c>sandbox.vhdx</c> as a
/// differencing child over it) so HCS's synthetic storage accepts it; copy a
/// standalone donor container storage (fast path); copy a platform template; or
/// finally create a <b>blank dynamic VHDX</b> from scratch. The guest-state file
/// is copied from the same source when present, otherwise created with
/// <c>HcsCreateEmptyGuestStateFile</c>.
/// </para>
/// </summary>
public interface ISandboxStorageProvisioner
{
    /// <summary>
    /// Ensures the storage directory holds a <c>sandbox.vhdx</c> and
    /// <c>sandbox.vmgs</c>, building an uncompressed bootable chain from the
    /// platform base image when one can be found, otherwise creating them from
    /// scratch.
    /// </summary>
    /// <param name="storageDirectory">Directory to provision into.</param>
    /// <param name="diskSizeMb">
    /// Requested maximum disk size in MB; <c>0</c> selects the platform default
    /// (80&#160;GB, matching Windows Sandbox's own blank disk). This is
    /// <b>only applied when a blank disk is created from scratch</b>: when a
    /// base image, donor or template disk is used its existing geometry is
    /// preserved and this value is <b>ignored</b>.
    /// </param>
    StorageLayout Provision(string storageDirectory, uint diskSizeMb = 0);
}
