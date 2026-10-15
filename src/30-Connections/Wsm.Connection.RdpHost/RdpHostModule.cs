using Volo.Abp.Modularity;

namespace Wsm.Connection.RdpHost;

/// <summary>
/// ABP module for the RDP connector abstractions and the presentation selector.
/// <see cref="IRdpSessionHost"/> resolves to
/// <c>SelectingRdpSessionHost</c>, which delegates to the registered connector
/// module chosen by <see cref="IRdpConnectorPreference"/> (WPF by default).
/// </summary>
public sealed class RdpHostModule : AbpModule
{
}
