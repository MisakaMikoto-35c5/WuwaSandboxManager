using Volo.Abp.DependencyInjection;
using Wsm.Domain;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Application.EngineSelection;
using Wsm.Connection.Session;
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
    private readonly ISessionLauncher _sessionLauncher;

    public SandboxManager(
        ISandboxInstanceRepository repository,
        ISandboxEngineSelector selector,
        ISessionLauncher sessionLauncher)
    {
        _repository = repository;
        _selector = selector;
        _sessionLauncher = sessionLauncher;
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

    public async Task<SandboxInstanceDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        return ToDto(instance);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await _repository.GetAsync(new SandboxInstanceId(id), cancellationToken).ConfigureAwait(false);
        if (instance is null)
        {
            return;
        }

        if (instance.State.IsLive())
        {
            var engine = _selector.Select(RequestFor(instance));
            await engine.StopAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        }

        await _repository.DeleteAsync(instance.Id, cancellationToken).ConfigureAwait(false);
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

        // Deterministic single-instance pre-check. Retail Windows Sandbox hosts
        // report an *empty success* from the engine when a sandbox is already
        // running (the single-instance policy), so we ask the engine directly
        // before starting and fail deterministically.
        if (!engine.Capabilities.SupportsMultipleInstances)
        {
            var running = await engine.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            var others = running.Where(other => other != instance.Id).ToArray();
            if (others.Length > 0)
            {
                var message =
                    "This engine does not allow multiple concurrent instances. " +
                    $"Running instance(s): {string.Join(", ", others.Select(other => other.Value))}. " +
                    "Stop the running instance, or enable multi-instance support (see 'wsm multi').";
                instance.MarkFaulted(message);
                await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
                throw new MultipleInstancesNotAllowedException(message);
            }
        }

        try
        {
            var runtime = await engine.StartAsync(request, cancellationToken).ConfigureAwait(false);
            instance.MarkRunning(runtime);
        }
        catch (Exception ex)
        {
            instance.MarkFaulted(ex.Message);
            await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
            throw;
        }

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
        EnsureCheckpointsSupported(engine);
        return await engine.SaveCheckpointAsync(instance.Id, name, type, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SandboxCheckpoint>> ListCheckpointsAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        var engine = _selector.Select(RequestFor(instance));
        EnsureCheckpointsSupported(engine);
        return await engine.ListCheckpointsAsync(instance.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> RestoreCheckpointAsync(Guid id, Guid checkpointId, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        var engine = _selector.Select(RequestFor(instance));
        EnsureCheckpointsSupported(engine);

        var checkpoints = await engine.ListCheckpointsAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        var checkpoint = checkpoints.FirstOrDefault(candidate => candidate.Id == checkpointId)
            ?? throw new SandboxNotFoundException($"Checkpoint '{checkpointId}' was not found for sandbox '{id}'.");

        return await engine.RestoreCheckpointAsync(instance.Id, checkpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SandboxCapabilitiesDto> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var request = new SandboxStartRequest
        {
            Id = SandboxInstanceId.New(),
            Recipe = SandboxRecipe.Default,
        };

        var engine = _selector.Select(request);
        return await Task.FromResult(
            new SandboxCapabilitiesDto(engine.Name, engine.Kind, engine.IsAvailable, engine.Capabilities))
            .ConfigureAwait(false);
    }

    public async Task ConnectAsync(Guid id, SandboxConnectionMode mode, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        var engine = _selector.Select(RequestFor(instance));

        var runtime = instance.Runtime
            ?? await engine.GetRuntimeAsync(instance.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new SandboxDomainException($"Sandbox '{id}' is not running; start it before connecting.");

        if (instance.Runtime is null)
        {
            instance.AttachRuntime(runtime);
            await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        }

        await _sessionLauncher.LaunchAsync(instance.Id, runtime, mode, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetIpAddressesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        var engine = _selector.Select(RequestFor(instance));
        return await engine.GetIpAddressesAsync(instance.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task MapFolderAsync(Guid id, MappedFolder folder, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        var engine = _selector.Select(RequestFor(instance));

        await engine.MapFolderAsync(instance.Id, folder, cancellationToken).ConfigureAwait(false);
        instance.RecordMappedFolder(folder);
        await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ExecuteAsync(Guid id, string command, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        var engine = _selector.Select(RequestFor(instance));
        return await engine.ExecuteAsync(instance.Id, new LogonCommand(command), cancellationToken).ConfigureAwait(false);
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

    private static void EnsureCheckpointsSupported(ISandboxEngine engine)
    {
        if (!engine.Capabilities.SupportsCheckpoints)
        {
            throw new SandboxNotSupportedException(
                $"The engine '{engine.Name}' does not support checkpoints.");
        }
    }

    private static SandboxInstanceDto ToDto(SandboxInstance instance)
        => new(
            instance.Id.Value,
            instance.Name.Value,
            instance.State,
            instance.Recipe,
            instance.Runtime,
            instance.FaultReason,
            instance.CreatedUtc,
            instance.UpdatedUtc);
}
