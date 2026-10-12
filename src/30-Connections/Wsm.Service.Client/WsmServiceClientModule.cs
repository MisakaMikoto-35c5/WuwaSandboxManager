using Volo.Abp.Modularity;
using Wsm.Service.Contracts;

namespace Wsm.Service.Client;

/// <summary>
/// ABP module for the WSM agent client. Drives the resident
/// <c>Wsm.Service.Host</c> service through its named-pipe gRPC endpoint via
/// <see cref="IWsmServiceClient"/>.
/// </summary>
[DependsOn(typeof(WsmServiceContractsModule))]
public sealed class WsmServiceClientModule : AbpModule
{
}
