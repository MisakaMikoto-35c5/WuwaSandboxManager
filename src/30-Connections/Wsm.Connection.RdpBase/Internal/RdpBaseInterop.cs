using System.Runtime.InteropServices;

namespace Wsm.Connection.RdpBase.Internal;

/// <summary>
/// Interop surface for the undocumented <c>RDPBASE.dll</c> "Remote Desktop Base"
/// API. This is the transport plumbing that Windows Sandbox itself (and WDAG's
/// <c>HvsiRdpClient</c>) uses to carry an RDP session over a named pipe instead
/// of TCP, so a sandbox can be displayed with no guest networking at all.
/// </summary>
internal static class RdpBaseInterop
{
    // CLSID_RDPRuntimeSTAContext / CLSID_RDPENCNamedPipeDirectConnector
    public static readonly Guid PlatformContextClsid = new("FB332AE7-0055-4208-92B7-20410CA8382B");
    public static readonly Guid NamedPipeConnectorClsid = new("FB332AE7-0088-4208-92B7-20410CA8382B");

    [DllImport("RdpBase", ExactSpelling = true)]
    public static extern int RDPBASE_CreateInstance(
        nint platformContext,
        in Guid rclsid,
        in Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [ComImport]
    [Guid("FB332AE7-000E-4208-92B7-20410CA8382B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IRDPENCPlatformContext
    {
        // Nine placeholder slots preceding InitializeInstance (their exact
        // signatures are irrelevant; we only need the correct vtable layout).
        void GetProperty();
        void GetPropertyBool();
        void GetPropertyUnsignedLong();
        void GetPropertyInterface();
        void SetProperty();
        void SetPropertyBool();
        void SetPropertyUnsignedLong();
        void SetPropertyInterface();
        void SetPropertyIfNotPresent();

        [PreserveSig]
        int InitializeInstance();

        [PreserveSig]
        int TerminateInstance();
    }

    [ComImport]
    [Guid("4ACF942D-EADC-45BF-8EA8-793FE3CE31E8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IRDPENCNamedPipeDirectConnector
    {
        [PreserveSig]
        int InitializeInstance(IRDPENCNamedPipeDirectConnectorCallbacks callbacks);

        [PreserveSig]
        int TerminateInstance();

        [PreserveSig]
        int StartConnect([MarshalAs(UnmanagedType.LPWStr)] string pipeName);
    }

    [ComImport]
    [Guid("D923EFE9-0A6D-4344-92B3-164229DB8D2D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IRDPENCNamedPipeDirectConnectorCallbacks
    {
        [PreserveSig]
        void OnConnectionCompleted([MarshalAs(UnmanagedType.Interface)] object netStream);

        [PreserveSig]
        void OnConnectorError(int hr);
    }
}
