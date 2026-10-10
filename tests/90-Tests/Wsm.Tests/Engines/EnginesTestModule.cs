using Volo.Abp.Modularity;
using Wsm.Application;
using Wsm.Application.EngineSelection;
using Wsm.Engine.Hcs;
using Wsm.Engine.WindowsSandbox;
using Wsm.Persistence;

namespace Wsm.Tests.Engines;

/// <summary>
/// Test-only startup module that composes the Phase-4 engine graph: both
/// engines, the engine selector and the application layer (which pulls in
/// persistence). Booting it proves the module graph is acyclic and that the
/// engines register and resolve by convention.
/// </summary>
[DependsOn(
    typeof(HcsEngineModule),
    typeof(WindowsSandboxEngineModule),
    typeof(EngineSelectionModule),
    typeof(SandboxApplicationModule),
    typeof(SandboxPersistenceModule))]
public sealed class EnginesTestModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Keep the file repository from writing under %LocalAppData% during tests.
        Configure<SandboxPersistenceOptions>(options =>
            options.RootDirectory = Path.Combine(Path.GetTempPath(), "wsm-tests-" + Guid.NewGuid()));
    }
}
