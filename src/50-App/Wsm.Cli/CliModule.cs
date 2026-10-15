using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.Modularity;
using Wsm.Application;
using Wsm.Application.EngineSelection;
using Wsm.Connection.ContainerDesktop;
using Wsm.Engine.Hcs;
using Wsm.Engine.WindowsContainer;
using Wsm.Engine.WindowsSandbox;
using Wsm.Localization;
using Wsm.Service.Client;

namespace Wsm.Cli;

/// <summary>
/// Root startup module for the command line host. Composes the application layer
/// (which owns engine selection and persistence), all three sandbox engines and
/// the non-UI localization resource, then exposes the CLI through
/// <see cref="CliApplication"/>.
/// </summary>
[DependsOn(
    typeof(SandboxApplicationModule),
    typeof(EngineSelectionModule),
    typeof(HcsEngineModule),
    typeof(WindowsSandboxEngineModule),
    typeof(WindowsContainerEngineModule),
    typeof(WsmConnectionContainerDesktopModule),
    typeof(WsmLocalizationModule),
    typeof(WsmServiceClientModule))]
public sealed class CliModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The log level is Warning by default; set WSM_LOG_LEVEL (e.g. Debug) to
        // diagnose the engine/connection paths from the CLI.
        var level = Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("WSM_LOG_LEVEL"), ignoreCase: true, out var parsed)
            ? parsed
            : LogLevel.Warning;

        context.Services.AddLogging(builder => builder
            .AddSimpleConsole(options => options.SingleLine = true)
            .SetMinimumLevel(level));
    }
}
