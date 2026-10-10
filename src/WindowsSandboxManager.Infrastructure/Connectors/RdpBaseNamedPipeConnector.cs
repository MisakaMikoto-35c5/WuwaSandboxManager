using System.Runtime.InteropServices;

namespace WindowsSandboxManager.Infrastructure.Connectors;

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

/// <summary>
/// Reusable module that produces an RDP network-stream endpoint for a sandbox
/// named pipe (<c>\\.\pipe\&lt;VMId&gt;</c>). The returned endpoint can be fed to
/// the RDP ActiveX control via <c>AdvancedSettings.ConnectWithEndpoint</c>
/// (the same technique used by <c>WindowsSandboxRemoteSession</c> and by
/// third-party hosts such as NanaBox).
/// </summary>
public sealed class RdpBaseNamedPipeConnector : RdpBaseInterop.IRDPENCNamedPipeDirectConnectorCallbacks, IDisposable
{
    private RdpBaseInterop.IRDPENCNamedPipeDirectConnector? _connector;
    private readonly TaskCompletionSource<object> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RdpBaseNamedPipeConnector(RdpBaseInterop.IRDPENCNamedPipeDirectConnector connector)
    {
        _connector = connector;
        var hr = connector.InitializeInstance(this);
        if (hr < 0)
        {
            _completion.TrySetException(Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException("RDPBASE init failed."));
        }
    }

    /// <summary>Creates the platform context and named-pipe connector.</summary>
    public static RdpBaseNamedPipeConnector Create()
    {
        var hr = RdpBaseInterop.RDPBASE_CreateInstance(
            nint.Zero,
            RdpBaseInterop.PlatformContextClsid,
            typeof(RdpBaseInterop.IRDPENCPlatformContext).GUID,
            out var contextObject);
        if (hr < 0 || contextObject is not RdpBaseInterop.IRDPENCPlatformContext context)
        {
            throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException("Unable to create the RDP platform context.");
        }

        hr = context.InitializeInstance();
        if (hr < 0)
        {
            throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException("Unable to initialize the RDP platform context.");
        }

        var unknown = Marshal.GetIUnknownForObject(context);
        try
        {
            hr = RdpBaseInterop.RDPBASE_CreateInstance(
                unknown,
                RdpBaseInterop.NamedPipeConnectorClsid,
                typeof(RdpBaseInterop.IRDPENCNamedPipeDirectConnector).GUID,
                out var connectorObject);
            if (hr < 0 || connectorObject is not RdpBaseInterop.IRDPENCNamedPipeDirectConnector connector)
            {
                throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException("Unable to create the RDP named-pipe connector.");
            }

            return new RdpBaseNamedPipeConnector(connector);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>Starts the connect and returns the RDP net-stream endpoint.</summary>
    public async Task<object> GetEndpointAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        if (_connector is null)
        {
            throw new ObjectDisposedException(nameof(RdpBaseNamedPipeConnector));
        }

        var hr = _connector.StartConnect(pipeName);
        if (hr < 0)
        {
            throw Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException("StartConnect failed.");
        }

        return await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    void RdpBaseInterop.IRDPENCNamedPipeDirectConnectorCallbacks.OnConnectionCompleted(object netStream)
        => _completion.TrySetResult(netStream);

    void RdpBaseInterop.IRDPENCNamedPipeDirectConnectorCallbacks.OnConnectorError(int hr)
        => _completion.TrySetException(
            Marshal.GetExceptionForHR(hr) ?? new InvalidOperationException($"RDP named-pipe connector error 0x{hr:X8}."));

    public void Dispose()
    {
        try
        {
            Interlocked.Exchange(ref _connector, null)?.TerminateInstance();
        }
        catch
        {
            // ignore
        }
    }
}
