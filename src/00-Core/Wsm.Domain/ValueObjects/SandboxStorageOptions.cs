using WindowsSandboxManager.Domain.Exceptions;

namespace WindowsSandboxManager.Domain.ValueObjects;

/// <summary>
/// Storage configuration for a sandbox's user-data disk.
/// <para>
/// Windows Sandbox stores user data in
/// <c>C:\ProgramData\Microsoft\Windows\Containers\ContainerStorages\&lt;id&gt;\sandbox.vhdx</c>.
/// The size is controllable through the UDK <c>VMOptions.MaxStorageSizeMB</c>;
/// the <b>location</b> is only controllable at the HCS level, by pointing
/// <c>VirtualMachine.Devices.Scsi.Attachments["0"].Path</c> (and/or
/// <c>CmsCreateStorage</c> / <c>CmsMapVirtualDiskToContainer</c>) at a custom path.
/// </para>
/// </summary>
public sealed record SandboxStorageOptions
{
    /// <summary>Custom folder for the user-data VHDX (HCS backend only). Null = platform default.</summary>
    public string? StoragePath { get; init; }

    /// <summary>Requested disk size in MB (0 = platform default).</summary>
    public uint DiskSizeMb { get; init; }

    /// <summary>Encrypt the user-data disk (where supported).</summary>
    public bool Encryption { get; init; }

    public static SandboxStorageOptions Default { get; } = new();

    public bool HasCustomLocation => !string.IsNullOrWhiteSpace(StoragePath);

    public SandboxStorageOptions WithPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new SandboxValidationException("Storage path must not be empty.");
        }

        return this with { StoragePath = path.Trim() };
    }
}
