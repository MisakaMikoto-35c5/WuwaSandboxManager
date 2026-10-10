using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.Modularity;
using Wsm.Application;
using Wsm.Application.EngineSelection;
using Wsm.Engine.Hcs;
using Wsm.Engine.WindowsContainer;
using Wsm.Engine.WindowsSandbox;
using Wsm.Localization;

namespace Wsm.Ui.WinUI;

/// <summary>
/// Root startup module for the WinUI 3 host. Composes the application layer
/// (which owns engine selection and persistence), all three sandbox engines and
/// the non-UI localization resource, then exposes the UI through
/// <c>App</c>/<c>MainWindow</c>.
/// </summary>
[DependsOn(
    typeof(SandboxApplicationModule),
    typeof(EngineSelectionModule),
    typeof(HcsEngineModule),
    typeof(WindowsSandboxEngineModule),
    typeof(WindowsContainerEngineModule),
    typeof(WsmLocalizationModule))]
public sealed class WinUiModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Keep the container's logging quiet: only warnings and above surface.
        context.Services.AddLogging(builder => builder
            .AddConsole()
            .SetMinimumLevel(LogLevel.Warning));
    }
}
