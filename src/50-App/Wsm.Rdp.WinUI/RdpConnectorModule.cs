using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.Modularity;
using Wsm.Application;
using Wsm.Application.EngineSelection;
using Wsm.Engine.Hcs;
using Wsm.Engine.WindowsContainer;
using Wsm.Engine.WindowsSandbox;
using Wsm.Localization;

namespace Wsm.Rdp.WinUI;

/// <summary>
/// Root startup module for the WinUI 3 RDP connector host. Composes the
/// application layer (which provides <c>IRdpSandboxControl</c> for the toolbar's
/// lifecycle buttons) and the engines, plus the non-UI localization resource.
/// <para>
/// The connector never resolves the runtime itself: the launching host hands it a
/// fully-projected <c>RdpSessionRequest</c>. The application layer is loaded only
/// so the quick controls can start, stop and restart the sandbox.
/// </para>
/// </summary>
[DependsOn(
    typeof(SandboxApplicationModule),
    typeof(EngineSelectionModule),
    typeof(HcsEngineModule),
    typeof(WindowsSandboxEngineModule),
    typeof(WindowsContainerEngineModule),
    typeof(WsmLocalizationModule))]
public sealed class RdpConnectorModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.AddLogging(builder => builder
            .AddConsole()
            .SetMinimumLevel(LogLevel.Warning));
    }
}
