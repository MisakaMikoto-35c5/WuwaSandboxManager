using Volo.Abp.Modularity;
using Wsm.Domain;

namespace Wsm.Service.Contracts;

/// <summary>
/// ABP module for the WSM agent gRPC contract. Exposes the generated client and
/// server stubs plus the shared domain &lt;-&gt; proto mapping and the
/// domain-exception &lt;-&gt; gRPC status convention to both the service host and
/// its clients.
/// </summary>
[DependsOn(typeof(SandboxDomainModule))]
public sealed class WsmServiceContractsModule : AbpModule
{
}
