using Volo.Abp.Modularity;
using Wsm.Application;
using Wsm.Application.EngineSelection;
using Wsm.Engine.Hcs;
using Wsm.Engine.WindowsContainer;
using Wsm.Engine.WindowsSandbox;
using Wsm.Hcs.Json.Container;
using Wsm.Hcs.Network;
using Wsm.Hcs.Process;
using Wsm.Persistence;

namespace Wsm.Tests.Engines;

/// <summary>
/// Test-only startup module that composes the engine graph: the two live engines,
/// the four reserved stub modules (Windows Container engine + container JSON,
/// process and network capabilities), the engine selector and the application
/// layer (which pulls in persistence). Booting it proves the module graph is
/// acyclic and that every engine registers and resolves by convention.
/// </summary>
[DependsOn(
    typeof(HcsEngineModule),
    typeof(WindowsSandboxEngineModule),
    typeof(WindowsContainerEngineModule),
    typeof(ContainerJsonModule),
    typeof(HcsProcessModule),
    typeof(HcsNetworkModule),
    typeof(EngineSelectionModule),
    typeof(SandboxApplicationModule),
    typeof(SandboxPersistenceModule))]
public sealed class EnginesTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Keep the file repository from writing under the real config & data folder during tests.
        Configure<SandboxPersistenceOptions>(options =>
            options.ConfigDataFolder = Path.Combine(Path.GetTempPath(), "wsm-tests-" + Guid.NewGuid()));
    }
}
