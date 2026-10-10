// Deviation note (gate-approved): RDPBASE inherently needs two separate
// operations — create the connector and connect it to a pipe — so this module
// exposes two public interfaces instead of the usual one. The factory is the
// ABP-registered singleton; the connector itself stays internal and is produced
// by the factory, so the public surface remains intentional and minimal.

namespace Wsm.Connection.RdpBase;

/// <summary>
/// A live RDP named-pipe connector. Starts a connect on a sandbox named pipe and
/// yields the RDP network-stream endpoint that can be fed to the RDP ActiveX
/// control via <c>AdvancedSettings.ConnectWithEndpoint</c>.
/// </summary>
public interface IRdpBaseConnector : IDisposable
{
    Task<object> GetEndpointAsync(string pipeName, CancellationToken cancellationToken = default);
}
