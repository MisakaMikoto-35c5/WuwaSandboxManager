using Volo.Abp.Modularity;
using Wsm.Host.Environment;
using Wsm.Recipe.Serialization;

namespace Wsm.Connection.Session;

/// <summary>
/// ABP module for the RDP session launch capability. Opens the user-visible
/// session through <see cref="ISessionLauncher"/> by launching the packaged
/// <c>WindowsSandboxRemoteSession.exe</c> client.
/// </summary>
[DependsOn(
    typeof(HostEnvironmentModule),
    typeof(WsbRecipeSerializationModule))]
public sealed class SessionLaunchModule : AbpModule
{
}
