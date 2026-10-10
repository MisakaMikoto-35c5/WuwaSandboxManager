using Wsm.Domain;
using Wsm.Domain.ValueObjects;

namespace Wsm.Persistence;

/// <summary>
/// Reads/writes the per-sandbox manifest (<c>&lt;id&gt;.wsbx</c>) and reports the
/// deterministic manifest path for an instance.
/// </summary>
public interface ISandboxManifestStore
{
    /// <summary>The manifest path for an instance. The file may not exist yet.</summary>
    string GetManifestPath(SandboxInstanceId id);

    /// <summary>Writes (or updates) the manifest for an instance.</summary>
    Task SaveAsync(SandboxInstance instance, CancellationToken cancellationToken = default);

    /// <summary>Deletes the manifest for an instance, when present.</summary>
    Task DeleteAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);
}
