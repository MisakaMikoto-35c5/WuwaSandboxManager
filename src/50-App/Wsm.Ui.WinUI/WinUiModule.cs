using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.Modularity;
using Wsm.Application;
using Wsm.Application.EngineSelection;
using Wsm.Engine.Hcs;
using Wsm.Engine.WindowsContainer;
using Wsm.Engine.WindowsSandbox;
using Wsm.Hcs.Layout;
using Wsm.Localization;
using Wsm.Persistence;
using Wsm.Service.Client;

namespace Wsm.Ui.WinUI;

/// <summary>
/// Root startup module for the WinUI 3 host. Composes the application layer
/// (which owns engine selection and persistence), all three sandbox engines,
/// the WSM agent client (so VM lifecycle can be routed to the resident agent)
/// and the non-UI localization resource, then exposes the UI through
/// <c>App</c>/<c>MainWindow</c>.
/// </summary>
[DependsOn(
    typeof(SandboxApplicationModule),
    typeof(EngineSelectionModule),
    typeof(HcsEngineModule),
    typeof(WindowsSandboxEngineModule),
    typeof(WindowsContainerEngineModule),
    typeof(WsmServiceClientModule),
    typeof(WsmLocalizationModule))]
public sealed class WinUiModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // Keep the container's logging quiet: only warnings and above surface.
        context.Services.AddLogging(builder => builder
            .AddConsole()
            .SetMinimumLevel(LogLevel.Warning));

        // Resolve the config & data folder once, at startup, from the persisted
        // user settings, then point both persistence and the HCS layout at it. A
        // change persisted from the settings dialog takes effect after a relaunch.
        var settings = UiSettingsStore.Load();
        var configDataFolder = settings.ResolveConfigDataFolder();
        Configure<SandboxPersistenceOptions>(options => options.ConfigDataFolder = configDataFolder);
        Configure<HcsHostLayoutOptions>(options => options.SandboxDataFolder = configDataFolder);

        // The process-isolation engine's base image (an imported Windows Server
        // layer). Kept in the environment too so the engine and the isolation
        // policy agree even before the options bind.
        if (!string.IsNullOrWhiteSpace(settings.ContainerBaseImagePath))
        {
            Configure<WindowsContainerEngineOptions>(options =>
                options.BaseImageLayerPath = settings.ContainerBaseImagePath);
            Environment.SetEnvironmentVariable(
                WindowsContainerEngineOptions.ImageOverrideVariable, settings.ContainerBaseImagePath);
        }
    }
}
