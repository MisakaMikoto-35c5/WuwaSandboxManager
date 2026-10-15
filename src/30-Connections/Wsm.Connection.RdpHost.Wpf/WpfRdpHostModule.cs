using Volo.Abp.Modularity;
using Wsm.Connection.RdpBase;
using Wsm.Connection.RdpHost;
using Wsm.Localization;

namespace Wsm.Connection.RdpHost.Wpf;

/// <summary>
/// ABP module for the WPF RDP connector. Registers the
/// <see cref="IRdpConnectorHost"/> that presents sessions in a WPF XAML window
/// (toolbar with quick lifecycle controls, full screen and live resize) hosting
/// the shared <c>mstscax</c> ActiveX control through a <c>WindowsFormsHost</c>.
/// </summary>
[DependsOn(
    typeof(RdpHostModule),
    typeof(RdpBaseModule),
    typeof(WsmLocalizationModule))]
public sealed class WpfRdpHostModule : AbpModule
{
}
