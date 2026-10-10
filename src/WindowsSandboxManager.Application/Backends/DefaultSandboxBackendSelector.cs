using WindowsSandboxManager.Application.Abstractions;
using WindowsSandboxManager.Domain.Abstractions;
using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;

namespace WindowsSandboxManager.Application.Backends;

/// <summary>
/// Default selector. It only considers <i>available</i> backends (e.g. HCS needs
/// elevation) and, when the recipe asks for a CPU configuration, prefers a
/// backend that can honour it. Registration order expresses preference, so
/// registering the HCS backend first makes it the primary one when elevated.
/// </summary>
public sealed class DefaultSandboxBackendSelector : ISandboxBackendSelector
{
    private readonly IReadOnlyList<ISandboxBackend> _backends;
    private readonly SandboxCpuConfiguration _defaultCpu;

    public DefaultSandboxBackendSelector(IEnumerable<ISandboxBackend> backends)
    {
        _backends = backends.ToArray();
        if (_backends.Count == 0)
        {
            throw new SandboxNotSupportedException("No sandbox backend is registered.");
        }

        _defaultCpu = SandboxCpuConfiguration.Default();
    }

    public ISandboxBackend Select(SandboxRecipe recipe)
    {
        var available = _backends.Where(b => b.IsAvailable).ToArray();
        if (available.Length == 0)
        {
            throw new SandboxNotSupportedException(
                "No sandbox backend is currently available. The HCS backend requires an elevated " +
                "(administrator) process; run as Administrator or use the gRPC backend.");
        }

        var wantsCpu = recipe.Cpu != _defaultCpu;
        if (wantsCpu)
        {
            var cpuCapable = available.FirstOrDefault(b => b.Capabilities.SupportsCpuConfiguration);
            if (cpuCapable is not null)
            {
                return cpuCapable;
            }
        }

        var wantsCustomStorage = recipe.Storage.HasCustomLocation;
        if (wantsCustomStorage)
        {
            var storageCapable = available.FirstOrDefault(b => b.Capabilities.SupportsCustomStorageLocation);
            if (storageCapable is not null)
            {
                return storageCapable;
            }
        }

        return available[0];
    }
}
