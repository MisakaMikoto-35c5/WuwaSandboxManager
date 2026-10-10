using Volo.Abp.DependencyInjection;
using WindowsSandboxManager.Domain;
using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Application.EngineSelection;
using Wsm.Engines.Abstractions;
using Wsm.Persistence;

namespace Wsm.Application.Internal;

/// <summary>
/// Default <see cref="ISandboxManager"/>. Orchestrates the sandbox aggregate,
/// the instance repository and the engine chosen by
/// <see cref="ISandboxEngineSelector"/>. All engine specific behaviour lives
/// behind <see cref="ISandboxEngine"/>; this class owns the lifecycle and the
/// domain invariants.
/// </summary>
[ExposeServices(typeof(ISandboxManager))]
internal sealed class SandboxManager : ISandboxManager, ISingletonDependency
{
    private readonly ISandboxInstanceRepository _repository;
    private readonly ISandboxEngineSelector _selector;

    public SandboxManager(ISandboxInstanceRepository repository, ISandboxEngineSelector selector)
    {
        _repository = repository;
        _selector = selector;
    }

    public async Task<SandboxInstanceDto> CreateAsync(string? name, SandboxRecipe recipe, CancellationToken cancellationToken = default)
    {
        var instance = SandboxInstance.Create(
            name is null ? null : new SandboxName(name),
            recipe);

        await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        return ToDto(instance);
    }

    public async Task<IReadOnlyList<SandboxInstanceDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var instances = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        return instances.Select(ToDto).ToArray();
    }

    public async Task<SandboxInstanceDto> StartAsync(Guid id, SandboxConnectionMode mode, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        instance.BeginStart();

        var request = new SandboxStartRequest
        {
            Id = instance.Id,
            Recipe = instance.Recipe.ForConnectionMode(mode),
            ConnectionMode = mode,
        };

        var engine = _selector.Select(request);
        var runtime = await engine.StartAsync(request, cancellationToken).ConfigureAwait(false);

        instance.MarkRunning(runtime);
        await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        return ToDto(instance);
    }

    public async Task StopAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);

        var engine = _selector.Select(RequestFor(instance));
        await engine.StopAsync(instance.Id, cancellationToken).ConfigureAwait(false);

        instance.MarkStopped();
        await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> ListRunningAsync(SandboxEngineKind? preferredEngine = null, CancellationToken cancellationToken = default)
    {
        var request = new SandboxStartRequest
        {
            Id = SandboxInstanceId.New(),
            Recipe = SandboxRecipe.Default,
            PreferredEngine = preferredEngine,
        };

        var engine = _selector.Select(request);
        var ids = await engine.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        return ids.Select(id => id.Value).ToArray();
    }

    public async Task<SandboxCheckpoint> SaveCheckpointAsync(
        Guid id,
        string name,
        SandboxCheckpointType type,
        CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        var engine = _selector.Select(RequestFor(instance));
        return await engine.SaveCheckpointAsync(instance.Id, name, type, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SandboxCheckpoint>> ListCheckpointsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        var engine = _selector.Select(RequestFor(instance));
        return await engine.ListCheckpointsAsync(instance.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> RestoreCheckpointAsync(Guid id, Guid checkpointId, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        var engine = _selector.Select(RequestFor(instance));

        var checkpoints = await engine.ListCheckpointsAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        var checkpoint = checkpoints.FirstOrDefault(candidate => candidate.Id == checkpointId)
            ?? throw new SandboxNotFoundException($"Checkpoint '{checkpointId}' was not found for sandbox '{id}'.");

        return await engine.RestoreCheckpointAsync(instance.Id, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SandboxInstance> GetRequiredAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await _repository.GetAsync(new SandboxInstanceId(id), cancellationToken).ConfigureAwait(false);
        return instance ?? throw new SandboxNotFoundException($"Sandbox '{id}' was not found.");
    }

    private static SandboxStartRequest RequestFor(SandboxInstance instance) => new()
    {
        Id = instance.Id,
        Recipe = instance.Recipe,
    };

    private static SandboxInstanceDto ToDto(SandboxInstance instance)
        => new(instance.Id.Value, instance.Name.Value, instance.State, instance.Recipe, instance.Runtime);
}
