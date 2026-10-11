using Volo.Abp.Modularity;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Container;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Network;
using Wsm.Hcs.Process;
using Wsm.Host.Elevation;

namespace Wsm.Engine.WindowsContainer;

/// <summary>
/// ABP module for the Windows container engine. Composes the HCS capability
/// modules (interop, host layout, container JSON, process execution, networking)
/// and host elevation, and exposes <see cref="ISandboxEngine"/> through
/// conventional DI.
/// </summary>
[DependsOn(
    typeof(SandboxEngineAbstractionsModule),
    typeof(HcsInteropModule),
    typeof(HcsLayoutModule),
    typeof(ContainerJsonModule),
    typeof(HcsProcessModule),
    typeof(HcsNetworkModule),
    typeof(ElevationModule))]
public sealed class WindowsContainerEngineModule : AbpModule
{
}
