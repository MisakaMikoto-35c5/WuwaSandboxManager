using Wsm.Domain.Exceptions;

namespace Wsm.Domain.ValueObjects;

/// <summary>
/// Networking policy for a sandbox instance.
/// Maps to the <c>&lt;Networking&gt;</c> element of a .wsb recipe.
/// </summary>
public enum NetworkingMode
{
    /// <summary>Use the platform default (networking enabled, Hyper-V default switch).</summary>
    Default = 0,

    /// <summary>Networking enabled (<c>&lt;Networking&gt;Enable&lt;/Networking&gt;</c>).</summary>
    Enabled = 1,

    /// <summary>Networking disabled (<c>&lt;Networking&gt;Disable&lt;/Networking&gt;</c>).</summary>
    Disabled = 2,
}

/// <summary>
/// Virtualized GPU policy. Maps to <c>&lt;vGPU&gt;</c>.
/// </summary>
public enum VirtualGpuMode
{
    Default = 0,
    Enabled = 1,
    Disabled = 2,
}

/// <summary>
/// CPU (virtual processor) configuration. Windows Sandbox does not expose this
/// through .wsb; it is only honoured by the low level HCS backend.
/// </summary>
public sealed record SandboxCpuConfiguration
{
    public const int MinimumCoreCount = 1;
    public const int MaximumCoreCount = 64;

    public int CoreCount { get; }

    public SandboxCpuConfiguration(int coreCount)
    {
        if (coreCount < MinimumCoreCount || coreCount > MaximumCoreCount)
        {
            throw new SandboxValidationException(
                $"CPU core count must be between {MinimumCoreCount} and {MaximumCoreCount} (got {coreCount}).");
        }

        CoreCount = coreCount;
    }

    public static SandboxCpuConfiguration Default() => new(Math.Max(1, Environment.ProcessorCount / 2));

    public override string ToString() => $"{CoreCount} vCPU";
}

/// <summary>
/// Memory configuration. Maps to <c>&lt;MemoryInMB&gt;</c>. Windows Sandbox bumps
/// anything below 2048 MB up to 2048 MB, so we validate the same floor.
/// </summary>
public sealed record SandboxMemoryConfiguration
{
    public const int MinimumMemoryMb = 2048;
    public const int MaximumMemoryMb = 256 * 1024;
    public const int DefaultMemoryMb = 4096;

    public int MemoryMb { get; }

    public SandboxMemoryConfiguration(int memoryMb)
    {
        if (memoryMb < MinimumMemoryMb || memoryMb > MaximumMemoryMb)
        {
            throw new SandboxValidationException(
                $"Memory must be between {MinimumMemoryMb} and {MaximumMemoryMb} MB (got {memoryMb}).");
        }

        MemoryMb = memoryMb;
    }

    public static SandboxMemoryConfiguration Default() => new(DefaultMemoryMb);

    public override string ToString() => $"{MemoryMb} MB";
}
