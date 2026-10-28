using Volo.Abp.Modularity;
using Wsm.Connection.Legacy;
using Wsm.Connection.RdpBase;
using Wsm.Connection.RdpHost;
using Wsm.Connection.RailClient;
using Wsm.Connection.RemoteApp;
using Wsm.Connection.RdpHost.WinUi;
using Wsm.Connection.RdpHost.Wpf;
using Wsm.Host.Environment;

namespace Wsm.Connection.Session;

/// <summary>
/// ABP module for the RDP session launch capability. Opens the user-visible
/// session through <see cref="ISessionLauncher"/>, routing by connection mode:
/// RemoteApp sessions go through the WDAG-style client
/// (<c>Wsm.Connection.RailClient</c>), full desktops through the selectable RDP
/// connector (WPF by default, WinUI 3 when preferred).
/// <para>
/// <c>Wsm.Connection.RemoteApp</c> stays in the graph for its shortcut writer
/// (desktop shortcuts that connect to a RAIL application) and as the
/// remoteapptool-style alternative kit (mstsc + generated <c>.rdp</c> + the
/// named-pipe bridge).
/// </para>
/// </summary>
[DependsOn(
    typeof(HostEnvironmentModule),
    typeof(RdpBaseModule),
    typeof(RdpHostModule),
    typeof(RailClientModule),
    typeof(RemoteAppModule),
    typeof(WpfRdpHostModule),
    typeof(WinUiRdpHostModule),
    typeof(LegacyLauncherModule))]
public sealed class SessionLaunchModule : AbpModule
{
}
