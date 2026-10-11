using Volo.Abp.Modularity;
using Wsm.Connection.Legacy;
using Wsm.Connection.RdpBase;
using Wsm.Connection.RdpHost;
using Wsm.Host.Environment;

namespace Wsm.Connection.Session;

/// <summary>
/// ABP module for the RDP session launch capability. Opens the user-visible
/// session through <see cref="ISessionLauncher"/>, routing by engine kind: the
/// Windows Sandbox engine uses the packaged client (open the sandbox by id, with
/// the legacy launcher as the last resort), while every other engine connects as
/// a plain RDP session over the direct RDPBASE named-pipe endpoint.
/// </summary>
[DependsOn(
    typeof(HostEnvironmentModule),
    typeof(RdpBaseModule),
    typeof(RdpHostModule),
    typeof(LegacyLauncherModule))]
public sealed class SessionLaunchModule : AbpModule
{
}
