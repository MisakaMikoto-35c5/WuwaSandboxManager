namespace Wsm.Hcs.Network;

/// <summary>
/// Creates and queries HNS (Host Network Service) endpoints for HCS compute
/// systems. Endpoints are the object HCS containers attach through
/// <c>Container.Networking.NetworkAdapters</c>.
/// </summary>
public interface IHnsNetworkService
{
    /// <summary>Creates a NAT endpoint on the given HNS network and returns its id.</summary>
    Task<string> CreateNatEndpointAsync(string networkId, CancellationToken ct = default);

    /// <summary>Returns the IP addresses assigned to the compute system.</summary>
    Task<IReadOnlyList<string>> GetIpAddressesAsync(string computeSystemId, CancellationToken ct = default);
}
