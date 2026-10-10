using Wsm.Domain;
using Wsm.Domain.ValueObjects;

namespace Wsm.Persistence;

/// <summary>Persistence for sandbox instance definitions.</summary>
public interface ISandboxInstanceRepository
{
    Task SaveAsync(SandboxInstance instance, CancellationToken cancellationToken = default);

    Task<SandboxInstance?> GetAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SandboxInstance>> ListAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);
}
