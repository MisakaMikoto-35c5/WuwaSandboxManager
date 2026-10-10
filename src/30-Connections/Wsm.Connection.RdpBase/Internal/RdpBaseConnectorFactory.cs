using System.Runtime.InteropServices;
using Volo.Abp.DependencyInjection;

namespace Wsm.Connection.RdpBase.Internal;

/// <summary>
/// Default <see cref="IRdpBaseConnectorFactory"/>. Creates the RDPBASE platform
/// context and named-pipe connector, then wraps them in a
/// <see cref="RdpBaseConnector"/>.
/// </summary>
[ExposeServices(typeof(IRdpBaseConnectorFactory))]
internal sealed class RdpBaseConnectorFactory : IRdpBaseConnectorFactory, ISingletonDependency
{
    /// <summary>Creates the platform context and named-pipe connector.</summary>
    public IRdpBaseConnector Create()
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

            return new RdpBaseConnector(connector);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
