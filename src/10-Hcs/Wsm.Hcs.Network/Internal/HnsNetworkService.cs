using System.Runtime.InteropServices;
using System.Text.Json;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;

namespace Wsm.Hcs.Network.Internal;

/// <summary>
/// Default <see cref="IHnsNetworkService"/>: creates NAT endpoints and reads the
/// IP addresses of a compute system's endpoints through the HNS JSON dispatcher
/// (<c>HNSCall</c> in <c>vmcompute.dll</c>). <c>HNS</c> owns the container's
/// network namespace, so the caller addresses it by compute-system id.
/// </summary>
[ExposeServices(typeof(IHnsNetworkService))]
internal sealed class HnsNetworkService : IHnsNetworkService, ISingletonDependency
{
    private const string EndpointsPath = "/endpoints/";

    public Task<string> CreateNatEndpointAsync(string networkId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(networkId))
        {
            throw new SandboxValidationException("An HNS network id is required to create an endpoint.");
        }

        var request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["Name"] = $"wsm-{Guid.NewGuid():N}",
            // The endpoint joins the given HNS network; HNS assigns the address.
            ["VirtualNetwork"] = networkId,
        });

        var output = Call("POST", EndpointsPath, request);

        var id = ReadEndpointId(output)
            ?? throw new SandboxBackendException(
                "HNS created an endpoint but returned no id.", 0);

        return Task.FromResult(id);
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

    /// <summary>
    /// Sends a request to HNS and returns the raw <c>Output</c> JSON. A transport
    /// failure or an HNS-level error (<c>Success == false</c>) is surfaced as a
    /// <see cref="SandboxBackendException"/>.
    /// </summary>
    private static string Call(string method, string path, string request)
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

    private static string? ReadEndpointId(string output)
    {
        using var json = JsonDocument.Parse(output);
        return json.RootElement.ValueKind == JsonValueKind.Object &&
               json.RootElement.TryGetProperty("ID", out var id) &&
               id.ValueKind == JsonValueKind.String
            ? id.GetString()
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
