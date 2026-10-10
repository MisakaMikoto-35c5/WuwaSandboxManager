using Volo.Abp.Modularity;
using WindowsSandboxManager.Domain;

namespace Wsm.Hcs.Interop;

/// <summary>
/// ABP module for the HCS interop capability. Owns the P/Invoke surface over
/// <c>computecore.dll</c> and the operation based runner exposed through
/// <see cref="IHcsClient"/>.
/// </summary>
[DependsOn(typeof(SandboxDomainModule))]
public sealed class HcsInteropModule : AbpModule
{
}
