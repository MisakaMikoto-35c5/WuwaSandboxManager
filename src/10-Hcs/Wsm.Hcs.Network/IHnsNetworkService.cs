namespace Wsm.Hcs.Network;

/// <summary>
/// Creates and queries HNS (Host Network Service) endpoints for HCS compute
/// systems. Endpoints are the object HCS containers attach through
/// <c>Container.Networking.NetworkAdapters</c> and sandbox VMs through
/// <c>VirtualMachine.Devices.NetworkAdapters</c>.
/// </summary>
public interface IHnsNetworkService
{
    /// <summary>
    /// Returns the id of an existing NAT/ICS network (e.g. the Hyper-V
    /// "Default Switch"), creating the <c>wsm-nat</c> NAT network when the host
    /// has none.
    /// </summary>
    Task<string> GetOrCreateNatNetworkIdAsync(CancellationToken ct = default);

    /// <summary>Creates a NAT endpoint on the given HNS network and returns its id.</summary>
    Task<string> CreateNatEndpointAsync(string networkId, CancellationToken ct = default);

    /// <summary>
    /// Creates a NAT endpoint with an explicit name, so a later process can find
    /// and delete it (HNS endpoints outlive the compute system otherwise).
    /// </summary>
    Task<string> CreateNatEndpointAsync(string networkId, string name, CancellationToken ct = default);

    /// <summary>Deletes the endpoint with the given id; missing endpoints are ignored.</summary>
    Task DeleteEndpointAsync(string endpointId, CancellationToken ct = default);

    /// <summary>Deletes every endpoint with the given name; absence is not an error.</summary>
    Task DeleteEndpointByNameAsync(string name, CancellationToken ct = default);

    /// <summary>Returns the IP addresses assigned to the compute system.</summary>
    Task<IReadOnlyList<string>> GetIpAddressesAsync(string computeSystemId, CancellationToken ct = default);
}
