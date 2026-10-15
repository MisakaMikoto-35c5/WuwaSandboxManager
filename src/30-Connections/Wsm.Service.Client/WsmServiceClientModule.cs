using Volo.Abp.Modularity;
using Wsm.Host.Elevation;
using Wsm.Service.Contracts;

namespace Wsm.Service.Client;

/// <summary>
/// ABP module for the WSM agent client. Drives the resident
/// <c>Wsm.Service.Host</c> service through its named-pipe gRPC endpoint via
/// <see cref="IWsmServiceClient"/>, and can spawn a standalone console agent on
/// demand when no agent is running (see <see cref="IStandaloneAgentStarter"/>).
/// </summary>
[DependsOn(typeof(WsmServiceContractsModule), typeof(ElevationModule))]
public sealed class WsmServiceClientModule : AbpModule
{
}
