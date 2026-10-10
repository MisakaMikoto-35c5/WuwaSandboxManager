using Volo.Abp.Modularity;
using Wsm.Host.Environment;

namespace Wsm.Connection.Session;

/// <summary>
/// ABP module for the RDP session launch capability. Opens the user-visible
/// session through <see cref="ISessionLauncher"/> by launching the packaged
/// <c>WindowsSandboxRemoteSession.exe</c> client.
/// </summary>
[DependsOn(typeof(HostEnvironmentModule))]
public sealed class SessionLaunchModule : AbpModule
{
}
