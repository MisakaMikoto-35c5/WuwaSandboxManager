using System.Runtime.InteropServices;
using System.Text.Json;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;

namespace Wsm.Hcs.Network.Internal;

/// <summary>
/// Default <see cref="IHnsNetworkService"/>: manages NAT networks and endpoints
/// through the HNS JSON dispatcher (<c>HNSCall</c> in <c>vmcompute.dll</c>).
/// <c>HNS</c> owns the container's network namespace, so the caller addresses it
/// by compute-system id.
/// </summary>
[ExposeServices(typeof(IHnsNetworkService))]
internal sealed class HnsNetworkService : IHnsNetworkService, ISingletonDependency
{
    private const string NetworksPath = "/networks/";
    private const string EndpointsPath = "/endpoints/";
    private const string NamespacesPath = "/namespaces/";

    /// <summary>
    /// The network created when the host has no NAT/ICS network of its own. An
    /// existing network (the Hyper-V "Default Switch" is type ICS) is always
    /// preferred so we neither fragment address space nor fight WinNAT setup.
    /// </summary>
    private const string FallbackNetworkName = "wsm-nat";
    private const string FallbackSubnetPrefix = "192.168.248.0/24";
    private const string FallbackGateway = "192.168.248.1";

    public Task<string> GetOrCreateNatNetworkIdAsync(CancellationToken ct = default)
    {
        var networks = ReadNetworks();

        // The Hyper-V "Default Switch" (ICS) is preferred: it runs DHCP, which
        // is what a sandbox VM's NIC needs to obtain an address. A NAT network
        // is the fallback, and the fallback network is created when neither
        // exists.
        var ics = networks.FirstOrDefault(network => network.Type == NetworkTypeIcs);
        if (ics is not null)
        {
            return Task.FromResult(ics.Id);
        }

        var nat = networks.FirstOrDefault(network => network.Type == NetworkTypeNat);
        if (nat is not null)
        {
            return Task.FromResult(nat.Id);
        }

        return Task.FromResult(CreateFallbackNatNetwork());
    }

    public Task<string> GetOrCreateContainerNetworkIdAsync(CancellationToken ct = default)
    {
        var networks = ReadNetworks();

        // A container's address is assigned by the network: a real NAT network
        // carries it on the HNS endpoint immediately, whereas an ICS network
        // leaves it to DHCP (and HNS reports no address). Prefer NAT, and create
        // the fallback NAT when only ICS exists.
        var nat = networks.FirstOrDefault(network => network.Type == NetworkTypeNat);
        if (nat is not null)
        {
            return Task.FromResult(nat.Id);
        }

        return Task.FromResult(CreateFallbackNatNetwork());
    }

    private static string CreateFallbackNatNetwork()
    {
        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Name"] = FallbackNetworkName,
            ["Type"] = "NAT",
            ["Subnets"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["AddressPrefix"] = FallbackSubnetPrefix,
                    ["GatewayAddress"] = FallbackGateway,
                },
            },
        });

        var output = Call("POST", NetworksPath, request);
        return ReadId(output)
            ?? throw new SandboxBackendException("HNS created a network but returned no id.", 0);
    }

    public Task<string> CreateNatEndpointAsync(string networkId, CancellationToken ct = default)
        => CreateNatEndpointAsync(networkId, name: null, ct);

    public Task<string> CreateNatEndpointAsync(string networkId, string? name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(networkId))
        {
            throw new SandboxValidationException("An HNS network id is required to create an endpoint.");
        }

        var request = new Dictionary<string, object?>
        {
            // The endpoint joins the given HNS network; HNS assigns the address.
            ["VirtualNetwork"] = networkId,
        };
        if (!string.IsNullOrWhiteSpace(name))
        {
            request["Name"] = name;
        }

        var output = Call("POST", EndpointsPath, JsonSerializer.Serialize(request));

        var id = ReadId(output)
            ?? throw new SandboxBackendException(
                "HNS created an endpoint but returned no id.", 0);

        return Task.FromResult(id);
    }

    public Task DeleteEndpointAsync(string endpointId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return Task.CompletedTask;
        }

        Call("DELETE", EndpointsPath + endpointId, string.Empty, allowEmpty: true);
        return Task.CompletedTask;
    }

    public Task DeleteEndpointByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.CompletedTask;
        }

        foreach (var endpoint in ReadEndpoints())
        {
            if (string.Equals(endpoint.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                Call("DELETE", EndpointsPath + endpoint.Id, string.Empty, allowEmpty: true);
            }
        }

        return Task.CompletedTask;
    }

    public Task<HnsEndpoint?> GetEndpointByNameAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new SandboxValidationException("An endpoint name is required to query HNS endpoints.");
        }

        var output = Call("GET", EndpointsPath, string.Empty);
        using var json = JsonDocument.Parse(output);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Task.FromResult<HnsEndpoint?>(null);
        }

        foreach (var endpoint in json.RootElement.EnumerateArray())
        {
            var endpointName = ReadString(endpoint, "Name");
            if (!string.Equals(endpointName, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Task.FromResult<HnsEndpoint?>(new HnsEndpoint(
                ReadString(endpoint, "ID") ?? string.Empty,
                endpointName,
                ReadString(endpoint, "MacAddress"),
                ReadString(endpoint, "IPAddress")));
        }

        return Task.FromResult<HnsEndpoint?>(null);
    }

    public Task<string> CreateNamespaceAsync(CancellationToken ct = default)
    {
        var output = Call("POST", NamespacesPath, "{\"SchemaVersion\":{\"Major\":2,\"Minor\":1}}");
        using var json = JsonDocument.Parse(output);
        var id = ReadString(json.RootElement, "ID");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new SandboxBackendException("HNS created a namespace but returned no id.", 0);
        }

        return Task.FromResult(id);
    }

    public Task AddEndpointToNamespaceAsync(string namespaceId, string endpointId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(namespaceId))
        {
            throw new SandboxValidationException("A namespace id is required to add an endpoint.");
        }

        if (string.IsNullOrWhiteSpace(endpointId))
        {
            throw new SandboxValidationException("An endpoint id is required to add it to a namespace.");
        }

        // The dispatcher expects the older { Type, Data: { Id } } shape.
        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Type"] = "Endpoint",
            ["Data"] = new Dictionary<string, object?> { ["Id"] = endpointId },
        });

        Call("POST", NamespacesPath + namespaceId + "/addresource", request);
        return Task.CompletedTask;
    }

    public Task DeleteNamespaceAsync(string namespaceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(namespaceId))
        {
            return Task.CompletedTask;
        }

        Call("DELETE", NamespacesPath + namespaceId, string.Empty, allowEmpty: true);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> GetIpAddressesAsync(string computeSystemId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(computeSystemId))
        {
            throw new SandboxValidationException("A compute system id is required to query HNS addresses.");
        }

        var output = Call("GET", EndpointsPath, string.Empty);
        var addresses = ReadAddresses(output, computeSystemId);
        return Task.FromResult<IReadOnlyList<string>>(addresses);
    }

    private const string NetworkTypeNat = "NAT";
    private const string NetworkTypeIcs = "ICS";

    private sealed record NamedId(string Id, string? Name, string? Type);

    private IReadOnlyList<NamedId> ReadNetworks() => ReadNamedIds(Call("GET", NetworksPath, string.Empty));

    private IReadOnlyList<NamedId> ReadEndpoints() => ReadNamedIds(Call("GET", EndpointsPath, string.Empty));

    /// <summary>Parses a JSON array of HNS objects into id/name/type triples.</summary>
    private static IReadOnlyList<NamedId> ReadNamedIds(string output)
    {
        var results = new List<NamedId>();
        using var json = JsonDocument.Parse(output);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var element in json.RootElement.EnumerateArray())
        {
            var id = ReadString(element, "ID");
            if (id is null)
            {
                continue;
            }

            results.Add(new NamedId(id, ReadString(element, "Name"), ReadString(element, "Type")));
        }

        return results;
    }

    /// <summary>
    /// Sends a request to HNS and returns the raw <c>Output</c> JSON. A transport
    /// failure or an HNS-level error (<c>Success == false</c>) is surfaced as a
    /// <see cref="SandboxBackendException"/>. DELETE responses carry no output, so
    /// those calls pass <paramref name="allowEmpty"/> (not valid elsewhere: an
    /// empty response elsewhere means the dispatcher misbehaved).
    /// </summary>
    private static string Call(string method, string path, string request, bool allowEmpty = false)
    {
        var hr = HnsNative.HNSCall(method, path, request, out var responsePtr);
        var document = TakeString(responsePtr);

        if (hr < 0)
        {
            throw new SandboxBackendException(
                $"HNSCall '{method} {path}' failed with HRESULT 0x{hr:X8}.", hr);
        }

        if (string.IsNullOrWhiteSpace(document))
        {
            if (allowEmpty)
            {
                return string.Empty;
            }

            throw new SandboxBackendException(
                $"HNSCall '{method} {path}' returned an empty response.", 0);
        }

        try
        {
            using var envelope = JsonDocument.Parse(document);
            var root = envelope.RootElement;

            if (root.TryGetProperty("Success", out var success) &&
                success.ValueKind == JsonValueKind.False)
            {
                var error = root.TryGetProperty("Error", out var e) ? e.GetString() : null;
                throw new SandboxBackendException(
                    $"HNS rejected '{method} {path}': {error ?? "unknown error"}.", 0);
            }

            return root.TryGetProperty("Output", out var output) ? output.GetRawText() : "null";
        }
        catch (JsonException ex)
        {
            throw new SandboxBackendException(
                $"HNS returned a malformed response for '{method} {path}'.", 0, ex);
        }
    }

    private static string? ReadId(string output)
    {
        using var json = JsonDocument.Parse(output);
        return ReadString(json.RootElement, "ID");
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    /// Selects the IPv4/IPv6 addresses of the endpoints attached to the given
    /// compute system. HNS associates an endpoint with its network namespace
    /// (<c>Namespace.ID</c>); process-isolated containers use the compute-system
    /// id as that namespace, so the two are matched here.
    /// </summary>
    private static IReadOnlyList<string> ReadAddresses(string output, string computeSystemId)
    {
        var results = new List<string>();
        using var json = JsonDocument.Parse(output);
        if (json.RootElement.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var endpoint in json.RootElement.EnumerateArray())
        {
            if (!BelongsToComputeSystem(endpoint, computeSystemId))
            {
                continue;
            }

            AddAddress(endpoint, "IPAddress", results);
            AddAddress(endpoint, "IPv6Address", results);
        }

        return results;
    }

    private static bool BelongsToComputeSystem(JsonElement endpoint, string computeSystemId)
    {
        if (endpoint.TryGetProperty("Namespace", out var ns) &&
            ns.ValueKind == JsonValueKind.Object &&
            ns.TryGetProperty("ID", out var id) &&
            id.ValueKind == JsonValueKind.String &&
            string.Equals(id.GetString(), computeSystemId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return endpoint.TryGetProperty("ComputeSystemId", out var cs) &&
               cs.ValueKind == JsonValueKind.String &&
               string.Equals(cs.GetString(), computeSystemId, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddAddress(JsonElement endpoint, string property, List<string> results)
    {
        if (!endpoint.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var address = value.GetString();
        if (!string.IsNullOrWhiteSpace(address))
        {
            results.Add(address);
        }
    }

    private static string? TakeString(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            // HNS allocates the response with CoTaskMemAlloc (unlike HCS, which
            // uses LocalAlloc and is freed with LocalFree).
            Marshal.FreeCoTaskMem(pointer);
        }
    }
}
