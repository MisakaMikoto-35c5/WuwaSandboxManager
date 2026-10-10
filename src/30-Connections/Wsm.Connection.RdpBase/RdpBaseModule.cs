using Volo.Abp.Modularity;

namespace Wsm.Connection.RdpBase;

/// <summary>
/// ABP module for the <c>RDPBASE.dll</c> named-pipe connector. Produces an RDP
/// network-stream endpoint for a sandbox named pipe through
/// <see cref="IRdpBaseConnectorFactory"/>.
/// </summary>
public sealed class RdpBaseModule : AbpModule
{
}
