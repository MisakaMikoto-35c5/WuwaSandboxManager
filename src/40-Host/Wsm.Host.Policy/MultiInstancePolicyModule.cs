using Volo.Abp.Modularity;
using Wsm.Host.Elevation;

namespace Wsm.Host.Policy;

/// <summary>
/// ABP module for the multi-instance policy capability. Reads the host's
/// concurrent-instance policy (WDAG/AVD or internal branch) and, only on explicit
/// administrator request, can toggle the <c>BuildBranch</c> registry value.
/// </summary>
[DependsOn(typeof(ElevationModule))]
public sealed class MultiInstancePolicyModule : AbpModule
{
}
