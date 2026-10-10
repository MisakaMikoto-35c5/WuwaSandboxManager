using Volo.Abp.Modularity;
using WindowsSandboxManager.Domain;

namespace Wsm.Hcs.Process;

/// <summary>
/// ABP module for the in-guest process execution capability.
/// <para>
/// <b>Reserved / not implemented yet.</b> Registered so the module graph is
/// complete; the executor throws
/// <see cref="WindowsSandboxManager.Domain.Exceptions.SandboxNotSupportedException"/>
/// until a later phase implements it.
/// </para>
/// </summary>
[DependsOn(typeof(SandboxDomainModule))]
public sealed class HcsProcessModule : AbpModule
{
}
