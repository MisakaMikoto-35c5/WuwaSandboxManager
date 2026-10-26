using System.Text.Json;

namespace Wsm.Hcs.Interop;

/// <summary>Summary of a compute system as returned by HCS.</summary>
public sealed record HcsComputeSystemInfo
{
    public required string Id { get; init; }

    public string? Owner { get; init; }

    public string? SystemType { get; init; }

    public string? State { get; init; }

    public string? Name { get; init; }

    public JsonElement? Raw { get; init; }

    public bool IsRunning => string.Equals(State, "Running", StringComparison.OrdinalIgnoreCase);

    public bool IsStopped => string.Equals(State, "Stopped", StringComparison.OrdinalIgnoreCase);

    public bool IsContainer => string.Equals(SystemType, "Container", StringComparison.OrdinalIgnoreCase);

    public bool IsVirtualMachine => string.Equals(SystemType, "VirtualMachine", StringComparison.OrdinalIgnoreCase);
}
