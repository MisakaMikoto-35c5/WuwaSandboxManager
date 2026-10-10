using WindowsSandboxManager.Domain.Exceptions;

namespace WindowsSandboxManager.Infrastructure.Hcs;

/// <summary>
/// Provisions the user-data storage for a sandbox at a chosen location. Windows
/// Sandbox's own stack always writes to
/// <c>...\ContainerStorages\&lt;id&gt;\sandbox.vhdx</c>; this helper makes the
/// <b>location</b> configurable by staging the disk/guest-state files wherever
/// the caller asks, seeding them from an existing container storage when
/// necessary.
/// </summary>
public sealed class HcsStorageManager
{
    private readonly HcsHostLayout _layout;

    public HcsStorageManager(HcsHostLayout layout) => _layout = layout;

    /// <summary>Ensures the storage directory holds a <c>sandbox.vhdx</c> and <c>sandbox.vmgs</c>.</summary>
    public StorageLayout Provision(string storageDirectory)
    {
        Directory.CreateDirectory(storageDirectory);

        var disk = Path.Combine(storageDirectory, "sandbox.vhdx");
        var guestState = Path.Combine(storageDirectory, "sandbox.vmgs");

        if (!File.Exists(disk))
        {
            var donor = FindDonorStorage();
            if (donor is null)
            {
                throw new SandboxNotSupportedException(
                    "No user-data disk (sandbox.vhdx) is available to seed the storage location. " +
                    "Start a Windows Sandbox once (or provide an existing sandbox.vhdx) so the base storage can be created.");
            }

            File.Copy(Path.Combine(donor, "sandbox.vhdx"), disk);
            var donorGuestState = Path.Combine(donor, "sandbox.vmgs");
            if (File.Exists(donorGuestState) && !File.Exists(guestState))
            {
                File.Copy(donorGuestState, guestState);
            }
        }

        return new StorageLayout(storageDirectory, disk, guestState);
    }

    private string? FindDonorStorage()
    {
        try
        {
            if (!Directory.Exists(_layout.StorageRoot))
            {
                return null;
            }

            foreach (var directory in Directory.EnumerateDirectories(_layout.StorageRoot))
            {
                if (File.Exists(Path.Combine(directory, "sandbox.vhdx")))
                {
                    return directory;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }
}

/// <summary>Resolved storage paths for a sandbox.</summary>
public sealed record StorageLayout(string Directory, string DiskPath, string GuestStatePath);
