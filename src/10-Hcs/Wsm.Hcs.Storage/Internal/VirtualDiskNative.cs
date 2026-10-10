using System.Runtime.InteropServices;

namespace Wsm.Hcs.Storage.Internal;

/// <summary>
/// P/Invoke surface over <c>virtdisk.dll</c> (VHD/VHDX management). Only the
/// minimum needed to create a blank dynamic VHDX is declared.
/// </summary>
internal static class VirtualDiskNative
{
    private const string Virtdisk = "virtdisk.dll";
    private const string Kernel32 = "kernel32.dll";

    /// <summary><c>VIRTUAL_STORAGE_TYPE_DEVICE_VHDX</c>.</summary>
    public const uint DeviceVhdx = 3;

    /// <summary><c>VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT</c> = {EC984AEC-A0F9-47E9-901F-71415A66345B}.</summary>
    public static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47E9-901F-71415A66345B");

    /// <summary><c>CREATE_VIRTUAL_DISK_VERSION_1</c>.</summary>
    public const int CreateVirtualDiskVersion1 = 1;

    /// <summary>
    /// <c>VIRTUAL_DISK_ACCESS_CREATE</c> — the access mask
    /// <c>CreateVirtualDisk</c> requires to create a blank disk. Passing
    /// <c>VIRTUAL_DISK_ACCESS_NONE</c> (0) fails with <c>E_ACCESSDENIED</c>
    /// (0x80070005).
    /// </summary>
    public const uint VirtualDiskAccessCreate = 0x00100000;

    /// <summary><c>CREATE_VIRTUAL_DISK_FLAG_NONE</c> — creates a dynamic disk.</summary>
    public const uint CreateVirtualDiskFlagNone = 0;

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct VirtualStorageType
    {
        [FieldOffset(0)] public uint DeviceId;

        [FieldOffset(4)] public Guid VendorId;
    }

    /// <summary>
    /// <c>CREATE_VIRTUAL_DISK_PARAMETERS</c> with the <b>Version 1</b> layout.
    /// Version 1 contains <c>UniqueId, MaximumSize, BlockSizeInBytes,
    /// SectorSizeInBytes, ParentPath, SourcePath</c>; the trailing pointers
    /// must be present so the struct size matches the native ABI (56 bytes on
    /// x64).
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 56)]
    public struct CreateVirtualDiskParameters
    {
        [FieldOffset(0)] public int Version;

        [FieldOffset(8)] public Guid UniqueId;

        [FieldOffset(24)] public ulong MaximumSize;

        [FieldOffset(32)] public uint BlockSizeInBytes;

        [FieldOffset(36)] public uint SectorSizeInBytes;

        [FieldOffset(40)] public IntPtr ParentPath;

        [FieldOffset(48)] public IntPtr SourcePath;
    }

    [DllImport(Virtdisk, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int CreateVirtualDisk(
        ref VirtualStorageType virtualStorageType,
        string path,
        uint virtualDiskAccessMask,
        IntPtr securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        ref CreateVirtualDiskParameters parameters,
        IntPtr overlapped,
        out IntPtr handle);

    [DllImport(Kernel32, SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);
}
