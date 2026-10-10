using Volo.Abp.Modularity;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Container;
using Wsm.Hcs.Network;
using Wsm.Hcs.Process;
using Wsm.Hcs.Storage;

namespace Wsm.Engine.WindowsContainer;

/// <summary>
/// ABP module for the Windows container (lightweight isolation) engine.
/// <para>
/// <b>Reserved / not implemented yet.</b> The engine registers and advertises
/// its planned capability matrix, but reports <c>IsAvailable == false</c> and
/// throws <see cref="Wsm.Domain.Exceptions.SandboxNotSupportedException"/>
/// from every runtime member until a later phase implements it.
/// </para>
/// </summary>
[DependsOn(
    typeof(SandboxEngineAbstractionsModule),
    typeof(HcsInteropModule),
    typeof(ContainerJsonModule),
    typeof(HcsStorageModule),
    typeof(HcsProcessModule),
    typeof(HcsNetworkModule))]
public sealed class WindowsContainerEngineModule : AbpModule
{
}
