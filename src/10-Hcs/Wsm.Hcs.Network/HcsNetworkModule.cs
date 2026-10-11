using Volo.Abp.Modularity;
using Wsm.Domain;

namespace Wsm.Hcs.Network;

/// <summary>
/// ABP module for the HNS networking capability. It exposes
/// <see cref="IHnsNetworkService"/>, which calls the Host Network Service JSON
/// dispatcher (<c>HNSCall</c> in <c>vmcompute.dll</c>).
/// </summary>
[DependsOn(typeof(SandboxDomainModule))]
public sealed class HcsNetworkModule : AbpModule
{
}
