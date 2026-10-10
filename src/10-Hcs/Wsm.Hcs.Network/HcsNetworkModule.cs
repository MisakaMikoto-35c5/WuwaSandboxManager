using Volo.Abp.Modularity;
using Wsm.Domain;

namespace Wsm.Hcs.Network;

/// <summary>
/// ABP module for the HNS networking capability.
/// <para>
/// <b>Reserved / not implemented yet.</b> Registered so the module graph is
/// complete; the service throws
/// <see cref="Wsm.Domain.Exceptions.SandboxNotSupportedException"/>
/// until a later phase implements it.
/// </para>
/// </summary>
[DependsOn(typeof(SandboxDomainModule))]
public sealed class HcsNetworkModule : AbpModule
{
}
