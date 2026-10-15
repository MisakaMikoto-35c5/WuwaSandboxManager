using System.Runtime.InteropServices;

namespace Wsm.Hcs.Storage.Internal;

/// <summary>
/// P/Invoke surface over the WCOW layer APIs exported by <c>vmcompute.dll</c>.
/// These are the same entry points <c>hcsshim</c> and the Windows container
/// manager use; they are undocumented and require elevation.
/// <para>
/// <c>DriverInfo.Type</c> is the graph-driver flavour (<c>1</c> = filter driver,
/// the value <c>hcsshim</c>'s <c>stdDriverInfo</c> uses) and <c>HomeDir</c> must
/// point at a UTF-16 <c>NUL</c> (not <c>null</c>).
/// </para>
/// </summary>
internal static class WcowLayerNative
{
    private const string VmCompute = "vmcompute.dll";

    /// <summary>Mirrors the native <c>DriverInfo</c> struct.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DriverInfo
    {
        public int Type;
        public IntPtr HomeDir;
    }

    /// <summary>Mirrors the native <c>WC_LAYER_DESCRIPTOR</c> struct.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct LayerDescriptor
    {
        public Guid LayerId;
        public uint Flags;
        public IntPtr Path;
    }

    [DllImport(VmCompute, ExactSpelling = true, SetLastError = true)]
    public static extern int NameToGuid(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        out Guid guid);

    [DllImport(VmCompute, ExactSpelling = true, SetLastError = true)]
    public static extern int CreateSandboxLayer(
        ref DriverInfo info,
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        IntPtr parent,
        [In] LayerDescriptor[] descriptors,
        UIntPtr count);

    [DllImport(VmCompute, ExactSpelling = true, SetLastError = true)]
    public static extern int ActivateLayer(
        ref DriverInfo info,
        [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport(VmCompute, ExactSpelling = true, SetLastError = true)]
    public static extern int PrepareLayer(
        ref DriverInfo info,
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        [In] LayerDescriptor[] descriptors,
        UIntPtr count);

    [DllImport(VmCompute, ExactSpelling = true, SetLastError = true)]
    public static extern int UnprepareLayer(
        ref DriverInfo info,
        [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport(VmCompute, ExactSpelling = true, SetLastError = true)]
    public static extern int DeactivateLayer(
        ref DriverInfo info,
        [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport(VmCompute, ExactSpelling = true, SetLastError = true)]
    public static extern int DestroyLayer(
        ref DriverInfo info,
        [MarshalAs(UnmanagedType.LPWStr)] string id);

    [DllImport(VmCompute, ExactSpelling = true, SetLastError = true)]
    public static extern int GetLayerMountPath(
        ref DriverInfo info,
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        ref UIntPtr length,
        IntPtr buffer);

    [DllImport(VmCompute, ExactSpelling = true, SetLastError = true)]
    public static extern int ImportLayer(
        ref DriverInfo info,
        [MarshalAs(UnmanagedType.LPWStr)] string id,
        [MarshalAs(UnmanagedType.LPWStr)] string importFolderPath,
        [In] LayerDescriptor[]? descriptors,
        UIntPtr count);

    [DllImport(VmCompute, ExactSpelling = true, SetLastError = true)]
    public static extern int ProcessBaseImage([MarshalAs(UnmanagedType.LPWStr)] string path);
}
