using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Container;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Network;
using Wsm.Hcs.Process;
using Wsm.Hcs.Storage;
using Wsm.Host.Elevation;

namespace Wsm.Engine.WindowsContainer;

/// <summary>
/// ABP module for the Windows container engine. Composes the HCS capability
/// modules (interop, host layout, container JSON, process execution, storage and
/// networking) and host elevation, and exposes <see cref="ISandboxEngine"/>
/// through conventional DI. <see cref="WindowsContainerEngineOptions"/> is bound
/// from the <c>WindowsContainer</c> configuration section (so a
/// <c>WindowsContainer__BaseImageLayerPath</c> environment variable also works).
/// </summary>
[DependsOn(
    typeof(SandboxEngineAbstractionsModule),
    typeof(HcsInteropModule),
    typeof(HcsLayoutModule),
    typeof(ContainerJsonModule),
    typeof(HcsProcessModule),
    typeof(HcsStorageModule),
    typeof(HcsNetworkModule),
    typeof(ElevationModule))]
public sealed class WindowsContainerEngineModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<WindowsContainerEngineOptions>(context.Services.GetConfiguration().GetSection("WindowsContainer"));
    }
}
