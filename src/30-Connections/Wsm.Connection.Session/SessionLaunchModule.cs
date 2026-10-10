using Volo.Abp.Modularity;
using Wsm.Connection.Legacy;
using Wsm.Connection.RdpBase;
using Wsm.Connection.RdpHost;
using Wsm.Host.Environment;

namespace Wsm.Connection.Session;

/// <summary>
/// ABP module for the RDP session launch capability. Opens the user-visible
/// session through <see cref="ISessionLauncher"/>, routing between the packaged
/// client full-desktop path (open the sandbox by id), the direct RDPBASE/RAIL
/// path and the legacy fallback (which starts a new sandbox).
/// </summary>
[DependsOn(
    typeof(HostEnvironmentModule),
    typeof(RdpBaseModule),
    typeof(RdpHostModule),
    typeof(LegacyLauncherModule))]
public sealed class SessionLaunchModule : AbpModule
{
}
