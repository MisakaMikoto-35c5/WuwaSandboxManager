// RDPBASE is a factory/product pair: one operation creates the named-pipe
// connector, and a separate operation connects it to a pipe. Two interfaces are
// therefore needed. The factory is the ABP-registered singleton; the connector
// itself stays internal and is produced by the factory, so the public surface
// remains minimal.

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
