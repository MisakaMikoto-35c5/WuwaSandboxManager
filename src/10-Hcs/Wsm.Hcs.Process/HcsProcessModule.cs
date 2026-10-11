using Volo.Abp.Modularity;
using Wsm.Domain;

namespace Wsm.Hcs.Process;

/// <summary>
/// ABP module for the in-compute-system process execution capability. It exposes
/// <see cref="IComputeProcessExecutor"/>, which drives the HCS process API
/// (<c>HcsCreateProcess</c> / <c>HcsWaitForProcessExit</c>) on
/// <c>computecore.dll</c>.
/// </summary>
[DependsOn(typeof(SandboxDomainModule))]
public sealed class HcsProcessModule : AbpModule
{
}
