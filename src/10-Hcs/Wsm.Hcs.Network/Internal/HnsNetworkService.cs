using Volo.Abp.DependencyInjection;
using WindowsSandboxManager.Domain.Exceptions;

namespace Wsm.Hcs.Network.Internal;

/// <summary>
/// Reserved implementation of <see cref="IHnsNetworkService"/>. Every member
/// throws until the capability is implemented.
/// </summary>
[ExposeServices(typeof(IHnsNetworkService))]
internal sealed class HnsNetworkService : IHnsNetworkService, ISingletonDependency
{
    private const string ReservedMessage =
        "Wsm.Hcs.Network is reserved / not implemented yet: HNS endpoint creation and IP queries will be implemented in a later phase.";

    public Task<string> CreateNatEndpointAsync(string networkId, CancellationToken ct = default)
        => throw new SandboxNotSupportedException(ReservedMessage);

    public Task<IReadOnlyList<string>> GetIpAddressesAsync(string computeSystemId, CancellationToken ct = default)
        => throw new SandboxNotSupportedException(ReservedMessage);
}
