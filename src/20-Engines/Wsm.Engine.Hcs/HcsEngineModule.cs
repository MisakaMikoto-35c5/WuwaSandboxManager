using Volo.Abp.Modularity;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Checkpoint;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Sandbox;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Storage;
using Wsm.Host.Elevation;

namespace Wsm.Engine.Hcs;

/// <summary>
/// ABP module for the HCS sandbox engine. Composes the HCS capability modules
/// (interop, layout, sandbox JSON, storage, checkpoint) plus host elevation and
/// exposes <see cref="ISandboxEngine"/> through conventional DI.
/// </summary>
[DependsOn(
    typeof(SandboxEngineAbstractionsModule),
    typeof(HcsInteropModule),
    typeof(HcsLayoutModule),
    typeof(SandboxVmJsonModule),
    typeof(HcsStorageModule),
    typeof(HcsCheckpointModule),
    typeof(ElevationModule))]
public sealed class HcsEngineModule : AbpModule
{
}
