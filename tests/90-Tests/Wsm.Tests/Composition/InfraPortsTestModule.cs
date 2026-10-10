using Volo.Abp.Modularity;
using Wsm.Connection.Legacy;
using Wsm.Connection.RdpBase;
using Wsm.Connection.Session;
using Wsm.Host.Elevation;
using Wsm.Host.Environment;
using Wsm.Host.Policy;
using Wsm.Persistence;
using Wsm.Recipe.Serialization;
using Wsm.SandboxServer.Client;

namespace Wsm.Tests.Composition;

/// <summary>
/// Test-only startup module that composes all nine Phase-3 infrastructure
/// modules. Booting it proves the module graph is acyclic and that every public
/// infrastructure port is registered and resolvable.
/// </summary>
[DependsOn(
    typeof(WsbRecipeSerializationModule),
    typeof(RdpBaseModule),
    typeof(LegacyLauncherModule),
    typeof(SessionLaunchModule),
    typeof(SandboxServerClientModule),
    typeof(MultiInstancePolicyModule),
    typeof(ElevationModule),
    typeof(HostEnvironmentModule),
    typeof(SandboxPersistenceModule))]
public sealed class InfraPortsTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Keep the file repository from writing under %LocalAppData% during tests.
        Configure<SandboxPersistenceOptions>(options =>
            options.RootDirectory = Path.Combine(Path.GetTempPath(), "wsm-tests-" + Guid.NewGuid()));
    }
}
