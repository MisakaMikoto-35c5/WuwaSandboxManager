using Wsm.Domain;
using Wsm.Domain.ValueObjects;

namespace Wsm.Persistence;

/// <summary>
/// Persistence for sandbox instances. Each sandbox is a single portable
/// <c>.wsm</c> file that embeds its complete configuration; the repository also
/// reads/indexes <c>.wsm</c> files opened from an arbitrary location.
/// </summary>
public interface ISandboxInstanceRepository
{
    Task SaveAsync(SandboxInstance instance, CancellationToken cancellationToken = default);

    Task<SandboxInstance?> GetAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SandboxInstance>> ListAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    /// <summary>The <c>.wsm</c> path for an instance. The file may not exist yet.</summary>
    string GetManifestPath(SandboxInstanceId id);

    /// <summary>
    /// Reads a <c>.wsm</c> from an arbitrary path (association / open dialog / cli
    /// argument), resolving its relative data path against the file, and indexes
    /// it so subsequent lifecycle operations address the same file.
    /// </summary>
    Task<SandboxInstance> ImportAsync(string manifestPath, CancellationToken cancellationToken = default);
}
