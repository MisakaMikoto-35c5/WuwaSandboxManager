using Volo.Abp.Modularity;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Sandbox;
using Wsm.Hcs.Layout;

namespace Wsm.Hcs.Checkpoint;

/// <summary>
/// ABP module for the HCS checkpoint capability. Saves, enumerates and restores
/// compute-system checkpoints through <see cref="ICheckpointService"/>.
/// </summary>
[DependsOn(
    typeof(HcsInteropModule),
    typeof(HcsLayoutModule),
    typeof(SandboxVmJsonModule))]
public sealed class HcsCheckpointModule : AbpModule
{
}
