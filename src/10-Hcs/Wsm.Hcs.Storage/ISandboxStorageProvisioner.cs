namespace Wsm.Hcs.Storage;

/// <summary>
/// Provisions the user-data storage for a sandbox at a chosen location. Windows
/// Sandbox's own stack always writes to
/// <c>...\ContainerStorages\&lt;id&gt;\sandbox.vhdx</c>; this makes the
/// <b>location</b> configurable by staging the disk/guest-state files wherever
/// the caller asks, seeding them from an existing container storage when
/// necessary.
/// </summary>
public interface ISandboxStorageProvisioner
{
    /// <summary>Ensures the storage directory holds a <c>sandbox.vhdx</c> and <c>sandbox.vmgs</c>.</summary>
    StorageLayout Provision(string storageDirectory);
}
