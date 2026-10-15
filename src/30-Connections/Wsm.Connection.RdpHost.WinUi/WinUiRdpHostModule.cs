using Volo.Abp.Modularity;
using Wsm.Connection.RdpHost;

namespace Wsm.Connection.RdpHost.WinUi;

/// <summary>
/// ABP module for the WinUI 3 RDP connector. Registers the
/// <see cref="IRdpConnectorHost"/> that launches the dedicated
/// <c>Wsm.Rdp.WinUI.exe</c> connector process and completes when its window
/// closes.
/// </summary>
[DependsOn(typeof(RdpHostModule))]
public sealed class WinUiRdpHostModule : AbpModule
{
}
