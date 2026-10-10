namespace Wsm.Connection.RdpBase;

/// <summary>
/// Creates <see cref="IRdpBaseConnector"/> instances by standing up the RDPBASE
/// platform context and named-pipe connector. Registered as a singleton because
/// the platform context is process-scoped.
/// </summary>
public interface IRdpBaseConnectorFactory
{
    IRdpBaseConnector Create();
}
