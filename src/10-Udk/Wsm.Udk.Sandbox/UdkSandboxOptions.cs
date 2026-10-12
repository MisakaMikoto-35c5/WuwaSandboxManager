namespace Wsm.Udk.Sandbox;

/// <summary>
/// The options used to create a UDK-managed Windows Sandbox VM. They map onto
/// <c>WindowsUdk.Security.Isolation.VMOptions</c>; the UDK derives the whole HCS
/// document (silo, devices, HvSocket, boot, …) from them, which is precisely the
/// part a hand-written HCS JSON gets wrong.
/// </summary>
public sealed record UdkSandboxOptions
{
    /// <summary>The VM display name (surfaces in <c>hcsdiag list</c>).</summary>
    public required string DisplayName { get; init; }

    /// <summary>Maximum guest memory in MB.</summary>
    public uint MemoryMb { get; init; } = 4096;

    /// <summary>Maximum user-data storage in MB. Zero leaves the UDK default.</summary>
    public uint StorageMb { get; init; }

    /// <summary>Virtual processor count. Zero leaves the UDK default.</summary>
    public uint CpuCount { get; init; }

    /// <summary>True to attach a NAT network adapter.</summary>
    public bool EnableNetworking { get; init; }

    /// <summary>True to mirror all host GPUs into the guest.</summary>
    public bool EnableVirtualGpu { get; init; }
}
