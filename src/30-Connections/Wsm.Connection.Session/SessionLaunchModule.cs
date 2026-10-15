using Volo.Abp.Modularity;
using Wsm.Connection.Legacy;
using Wsm.Connection.RdpBase;
using Wsm.Connection.RdpHost;
using Wsm.Connection.RdpHost.WinUi;
using Wsm.Connection.RdpHost.Wpf;
using Wsm.Host.Environment;

namespace Wsm.Connection.Session;

/// <summary>
/// ABP module for the RDP session launch capability. Opens the user-visible
/// session through <see cref="ISessionLauncher"/>: the live runtime is projected
/// into an <see cref="RdpSessionRequest"/> and handed to the selectable RDP
/// connector (WPF by default, WinUI 3 when preferred).
/// </summary>
[DependsOn(
    typeof(HostEnvironmentModule),
    typeof(RdpBaseModule),
    typeof(RdpHostModule),
    typeof(WpfRdpHostModule),
    typeof(WinUiRdpHostModule),
    typeof(LegacyLauncherModule))]
public sealed class SessionLaunchModule : AbpModule
{
}
