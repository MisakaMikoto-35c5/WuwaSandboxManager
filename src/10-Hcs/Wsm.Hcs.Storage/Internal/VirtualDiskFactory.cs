using System.Runtime.InteropServices;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;

namespace Wsm.Hcs.Storage.Internal;

/// <summary>
/// Default <see cref="IVirtualDiskFactory"/>: creates VHDX disks via the Win32
/// <c>VirtDisk</c> API (<c>CreateVirtualDisk</c>) using
/// <c>VIRTUAL_DISK_ACCESS_CREATE</c>, and reads parent links with
/// <see cref="VhdxParentLocator"/>. The HCS engine that consumes the disk still
/// requires an elevated process.
/// </summary>
[ExposeServices(typeof(IVirtualDiskFactory))]
internal sealed class VirtualDiskFactory : IVirtualDiskFactory, ISingletonDependency
{
    /// <summary>
    /// The maximum size Windows Sandbox encodes in its own blank
    /// <c>sandbox.vhdx</c> (80&#160;GB, read from the VHDX <c>VirtualDiskSize</c>
    /// metadata entry). The disk is dynamic, so only the used blocks are
    /// allocated on disk.
    /// </summary>
    public const ulong DefaultMaximumSizeBytes = 80UL * 1024 * 1024 * 1024;

    /// <summary>
    /// Block size (1&#160;MB) used for the blank dynamic VHDX. Windows Sandbox's
    /// own <c>sandbox.vhdx</c> uses 1&#160;MB blocks (read from the host VHDX
    /// metadata), whereas the provider default is 32&#160;MB; matching the
    /// platform avoids a geometry mismatch when HCS mounts the disk.
    /// </summary>
    public const uint DefaultBlockSizeInBytes = 1 * 1024 * 1024;

    public void CreateDynamicVhdx(string path, ulong maximumSizeBytes)
    {
        if (maximumSizeBytes == 0)
        {
            maximumSizeBytes = DefaultMaximumSizeBytes;
        }

        var parameters = new VirtualDiskNative.CreateVirtualDiskParameters
        {
            Version = VirtualDiskNative.CreateVirtualDiskVersion1,
            UniqueId = Guid.NewGuid(),
            MaximumSize = maximumSizeBytes,
            BlockSizeInBytes = DefaultBlockSizeInBytes, // 1 MB, matching Windows Sandbox
            SectorSizeInBytes = 0, // provider default (512)
            ParentPath = IntPtr.Zero,
            SourcePath = IntPtr.Zero,
        };

        CreateVirtualDisk(path, parameters, $"a blank dynamic VHDX at '{path}'");
    }

    public void CreateDifferencingVhdx(string path, string parentPath)
    {
        if (string.IsNullOrWhiteSpace(parentPath))
        {
            throw new SandboxValidationException(
                "A differencing VHDX requires a non-empty parent path.");
        }

        // For a differencing disk the geometry (maximum size, block and sector
        // size) is inherited from the parent, so all three are left zero and
        // only the parent path is set.
        var parentPointer = Marshal.StringToHGlobalUni(parentPath);
        try
        {
            var parameters = new VirtualDiskNative.CreateVirtualDiskParameters
            {
                Version = VirtualDiskNative.CreateVirtualDiskVersion1,
                UniqueId = Guid.NewGuid(),
                MaximumSize = 0,
                BlockSizeInBytes = 0,
                SectorSizeInBytes = 0,
                ParentPath = parentPointer,
                SourcePath = IntPtr.Zero,
            };

            CreateVirtualDisk(path, parameters, $"a differencing VHDX at '{path}' (parent '{parentPath}')");
        }
        finally
        {
            Marshal.FreeHGlobal(parentPointer);
        }
    }

    public string? TryGetParentPath(string vhdxPath) => VhdxParentLocator.TryGetAbsoluteParentPath(vhdxPath);

    private static void CreateVirtualDisk(
        string path,
        VirtualDiskNative.CreateVirtualDiskParameters parameters,
        string description)
    {
        var storageType = new VirtualDiskNative.VirtualStorageType
        {
            DeviceId = VirtualDiskNative.DeviceVhdx,
            VendorId = VirtualDiskNative.VendorMicrosoft,
        };

        var hresult = VirtualDiskNative.CreateVirtualDisk(
            ref storageType,
            path,
            VirtualDiskNative.VirtualDiskAccessCreate,
            IntPtr.Zero,
            VirtualDiskNative.CreateVirtualDiskFlagNone,
            0,
            ref parameters,
            IntPtr.Zero,
            out var handle);

        if (handle != IntPtr.Zero)
        {
            VirtualDiskNative.CloseHandle(handle);
        }

        if (hresult != 0)
        {
            throw new SandboxBackendException(
                $"Failed to create {description} (CreateVirtualDisk HRESULT 0x{hresult:X8}).",
                hresult);
        }
    }
}
