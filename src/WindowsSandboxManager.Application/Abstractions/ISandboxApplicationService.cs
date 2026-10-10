using WindowsSandboxManager.Application.Dtos;
using WindowsSandboxManager.Domain;
using WindowsSandboxManager.Domain.Abstractions;
using WindowsSandboxManager.Domain.ValueObjects;

namespace WindowsSandboxManager.Application.Abstractions;

/// <summary>
/// Primary (driving) port exposed by the application layer. The WinUI3 app and
/// the CLI talk to this, never directly to infrastructure.
/// </summary>
public interface ISandboxApplicationService
{
    Task<SandboxCapabilitiesDto> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SandboxInstanceDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>> ListRunningAsync(CancellationToken cancellationToken = default);

    Task<SandboxInstanceDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SandboxInstanceDto> CreateAsync(CreateSandboxRequest request, CancellationToken cancellationToken = default);

    Task<SandboxInstanceDto> UpdateAsync(Guid id, UpdateSandboxRequest request, CancellationToken cancellationToken = default);

    Task<SandboxInstanceDto> StartAsync(Guid id, SandboxConnectionMode mode, CancellationToken cancellationToken = default);

    Task StopAsync(Guid id, CancellationToken cancellationToken = default);

    Task ConnectAsync(Guid id, SandboxConnectionMode mode, CancellationToken cancellationToken = default);

    Task<SandboxRuntimeInfo?> GetRuntimeAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetIpAddressesAsync(Guid id, CancellationToken cancellationToken = default);

    Task MapFolderAsync(Guid id, MappedFolder folder, CancellationToken cancellationToken = default);

    Task<int> ExecuteAsync(Guid id, string command, CancellationToken cancellationToken = default);

    Task<SandboxCheckpoint> SaveCheckpointAsync(
        Guid id,
        string name,
        SandboxCheckpointType type,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SandboxCheckpoint>> ListCheckpointsAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<Guid> RestoreCheckpointAsync(
        Guid id,
        Guid checkpointId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Chooses the best backend for a given recipe.</summary>
public interface ISandboxBackendSelector
{
    ISandboxBackend Select(SandboxRecipe recipe);
}

/// <summary>Dispatches aggregate domain events after a use case completes.</summary>
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default);
}

/// <summary>No-op dispatcher used when no handler is registered.</summary>
public sealed class NullDomainEventDispatcher : IDomainEventDispatcher
{
    public Task DispatchAsync(IEnumerable<IDomainEvent> domainEvents, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
