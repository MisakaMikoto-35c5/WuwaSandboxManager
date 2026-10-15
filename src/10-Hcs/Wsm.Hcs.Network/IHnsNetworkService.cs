namespace Wsm.Hcs.Network;

/// <summary>
/// Creates and queries HNS (Host Network Service) endpoints and namespaces for
/// HCS compute systems. A process-isolated container attaches its NIC by joining
/// an HNS <b>namespace</b> (<c>Container.Networking.Namespace</c>), so the flow is
/// create namespace → create endpoint → add endpoint to namespace → create the
/// container with the namespace id. A sandbox VM attaches endpoints directly
/// through <c>VirtualMachine.Devices.NetworkAdapters</c>.
/// </summary>
public interface IHnsNetworkService
{
    /// <summary>
    /// Returns the id of an existing NAT/ICS network (e.g. the Hyper-V
    /// "Default Switch"), creating the <c>wsm-nat</c> NAT network when the host
    /// has none.
    /// </summary>
    Task<string> GetOrCreateNatNetworkIdAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns a network for a <b>container</b>: an existing NAT network when
    /// there is one, otherwise a freshly created <c>wsm-nat</c>. A NAT network
    /// assigns the container's address itself (HNS reports it on the endpoint),
    /// which an ICS network leaves to DHCP.
    /// </summary>
    Task<string> GetOrCreateContainerNetworkIdAsync(CancellationToken ct = default);

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

    /// <summary>
    /// Returns the endpoint with the given name, or null when absent. The MAC
    /// address pins the guest NIC the endpoint is attached to, which lets a
    /// caller identify the guest's address deterministically.
    /// </summary>
    Task<HnsEndpoint?> GetEndpointByNameAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Creates an HNS namespace and returns its id. A process-isolated container
    /// joins this namespace, and its endpoints are added to it, so the container
    /// gets the endpoint's NIC and address.
    /// </summary>
    Task<string> CreateNamespaceAsync(CancellationToken ct = default);

    /// <summary>Adds an existing endpoint to a namespace so it is visible inside it.</summary>
    Task AddEndpointToNamespaceAsync(string namespaceId, string endpointId, CancellationToken ct = default);

    /// <summary>Deletes a namespace; absence is not an error.</summary>
    Task DeleteNamespaceAsync(string namespaceId, CancellationToken ct = default);

    /// <summary>Returns the IP addresses assigned to the compute system.</summary>
    Task<IReadOnlyList<string>> GetIpAddressesAsync(string computeSystemId, CancellationToken ct = default);
}
