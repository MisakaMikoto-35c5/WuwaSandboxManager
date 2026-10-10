using Volo.Abp.DependencyInjection;
using WindowsSandboxManager.Domain.Exceptions;
using Wsm.Hcs.Layout;

namespace Wsm.Hcs.Storage.Internal;

/// <summary>
/// Default <see cref="ISandboxStorageProvisioner"/>. Seeds the user-data disk
/// from an existing container storage so the sandbox can be relocated.
/// </summary>
[ExposeServices(typeof(ISandboxStorageProvisioner))]
internal sealed class SandboxStorageProvisioner : ISandboxStorageProvisioner, ISingletonDependency
{
    private readonly IHcsHostLayout _layout;

    public SandboxStorageProvisioner(IHcsHostLayout layout) => _layout = layout;

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
