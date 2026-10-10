using Microsoft.Extensions.Logging;
using WindowsSandboxManager.Application.Abstractions;
using WindowsSandboxManager.Application.Dtos;
using WindowsSandboxManager.Application.Mapping;
using WindowsSandboxManager.Domain;
using WindowsSandboxManager.Domain.Abstractions;
using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;

namespace WindowsSandboxManager.Application;

/// <summary>
/// Orchestrates the sandbox use cases. It coordinates the domain aggregate, the
/// repository, the selected backend and the session connector, and translates
/// low level failures into domain exceptions.
/// </summary>
public sealed class SandboxApplicationService : ISandboxApplicationService
{
    private readonly ISandboxRepository _repository;
    private readonly ISandboxBackendSelector _backendSelector;
    private readonly ISandboxSessionConnector _connector;
    private readonly ISandboxEnvironment _environment;
    private readonly IDomainEventDispatcher _eventDispatcher;
    private readonly ILogger<SandboxApplicationService> _logger;

    public SandboxApplicationService(
        ISandboxRepository repository,
        ISandboxBackendSelector backendSelector,
        ISandboxSessionConnector connector,
        ISandboxEnvironment environment,
        IDomainEventDispatcher eventDispatcher,
        ILogger<SandboxApplicationService> logger)
    {
        _repository = repository;
        _backendSelector = backendSelector;
        _connector = connector;
        _environment = environment;
        _eventDispatcher = eventDispatcher;
        _logger = logger;
    }

    public Task<SandboxCapabilitiesDto> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var backend = _backendSelector.Select(SandboxRecipe.Default);
        var dto = new SandboxCapabilitiesDto(
            backend.Name,
            backend.Capabilities,
            new SandboxEnvironmentInfo(
                _environment.IsSupported,
                _environment.IsElevated,
                _environment.IsMultipleInstancesAllowed,
                _environment.PackageInstallPath,
                _environment.ServerPipeName,
                _environment.LegacyExecutablePath,
                _environment.RemoteSessionExecutablePath,
                _environment.ServerExecutablePath));
        return Task.FromResult(dto);
    }

    public async Task<IReadOnlyList<SandboxInstanceDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var instances = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        return instances.Select(i => i.ToDto()).ToArray();
    }

    public async Task<IReadOnlyList<Guid>> ListRunningAsync(CancellationToken cancellationToken = default)
    {
        var backend = _backendSelector.Select(SandboxRecipe.Default);
        var ids = await backend.EnumerateRunningAsync(cancellationToken).ConfigureAwait(false);
        return ids.Select(i => i.Value).ToArray();
    }

    public async Task<SandboxInstanceDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(id, cancellationToken).ConfigureAwait(false);
        return instance.ToDto();
    }

    public async Task<SandboxInstanceDto> CreateAsync(CreateSandboxRequest request, CancellationToken cancellationToken = default)
    {
        var name = string.IsNullOrWhiteSpace(request.Name) ? SandboxName.Default() : new SandboxName(request.Name);
        var recipe = request.Recipe ?? SandboxRecipe.Default;

        var instance = SandboxInstance.Create(name, recipe);
        await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        await DispatchAsync(instance, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Created sandbox {SandboxId} ({Name})", instance.Id, instance.Name);
        return instance.ToDto();
    }

    public async Task<SandboxInstanceDto> UpdateAsync(Guid id, UpdateSandboxRequest request, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(id, cancellationToken).ConfigureAwait(false);
        instance.ChangeConfiguration(request.Recipe);
        await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        await DispatchAsync(instance, cancellationToken).ConfigureAwait(false);
        return instance.ToDto();
    }

    public async Task<SandboxInstanceDto> StartAsync(Guid id, SandboxConnectionMode mode, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(id, cancellationToken).ConfigureAwait(false);
        var backend = _backendSelector.Select(instance.Recipe);

        // Windows Sandbox gates concurrency behind a host policy that is false on
        // retail builds. Enforce it deterministically here instead of relying on
        // the server, which reports an empty success in this situation.
        if (!backend.Capabilities.SupportsMultipleInstances)
        {
            var running = await backend.EnumerateRunningAsync(cancellationToken).ConfigureAwait(false);
            var others = running.Where(r => r != instance.Id).ToArray();
            if (others.Length > 0)
            {
                var message =
                    "The host policy only allows a single Windows Sandbox instance at a time. " +
                    $"Already running: {string.Join(", ", others.Select(o => o.Value))}. " +
                    "Stop the running instance, or enable multi-instance support (see 'wsm multi').";
                instance.MarkFaulted(message);
                await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
                throw new MultipleInstancesNotAllowedException(message);
            }
        }

        instance.BeginStart();
        try
        {
            var runtime = await backend
                .StartAsync(instance.Recipe.ForConnectionMode(mode), instance.Id, cancellationToken)
                .ConfigureAwait(false);

            instance.MarkRunning(runtime);
        }
        catch (MultipleInstancesNotAllowedException ex)
        {
            instance.MarkFaulted(ex.Message);
            await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            instance.MarkFaulted(ex.Message);
            await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        await DispatchAsync(instance, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Started sandbox {SandboxId} on backend {Backend}: {Runtime}",
            instance.Id, backend.Name, instance.Runtime);
        return instance.ToDto();
    }

    public async Task StopAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(id, cancellationToken).ConfigureAwait(false);
        var backend = _backendSelector.Select(instance.Recipe);

        await backend.ShutdownAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        instance.MarkStopped();

        await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        await DispatchAsync(instance, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Stopped sandbox {SandboxId}", instance.Id);
    }

    public async Task ConnectAsync(Guid id, SandboxConnectionMode mode, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(id, cancellationToken).ConfigureAwait(false);
        var backend = _backendSelector.Select(instance.Recipe);

        var runtime = instance.Runtime
            ?? await backend.GetRuntimeInfoAsync(instance.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new SandboxDomainException($"Sandbox '{id}' is not running; start it before connecting.");

        if (instance.Runtime is null)
        {
            instance.AttachRuntime(runtime);
            await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        }

        await _connector.ConnectAsync(instance.Id, instance.Recipe, runtime, mode, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SandboxRuntimeInfo?> GetRuntimeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(id, cancellationToken).ConfigureAwait(false);
        if (instance.Runtime is not null)
        {
            return instance.Runtime;
        }

        var backend = _backendSelector.Select(instance.Recipe);
        return await backend.GetRuntimeInfoAsync(instance.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetIpAddressesAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(id, cancellationToken).ConfigureAwait(false);
        var backend = _backendSelector.Select(instance.Recipe);
        return await backend.GetIpAddressesAsync(instance.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task MapFolderAsync(Guid id, MappedFolder folder, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(id, cancellationToken).ConfigureAwait(false);
        var backend = _backendSelector.Select(instance.Recipe);

        await backend.MapFolderAsync(instance.Id, folder, cancellationToken).ConfigureAwait(false);
        instance.RecordMappedFolder(folder);

        await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
        await DispatchAsync(instance, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ExecuteAsync(Guid id, string command, CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(id, cancellationToken).ConfigureAwait(false);
        var backend = _backendSelector.Select(instance.Recipe);
        return await backend.ExecuteAsync(instance.Id, new LogonCommand(command), cancellationToken).ConfigureAwait(false);
    }

    public async Task<SandboxCheckpoint> SaveCheckpointAsync(
        Guid id,
        string name,
        SandboxCheckpointType type,
        CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(id, cancellationToken).ConfigureAwait(false);
        var backend = _backendSelector.Select(instance.Recipe);
        var checkpoint = await backend.SaveCheckpointAsync(instance.Id, name, type, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Saved checkpoint {Checkpoint} ({Type}) for sandbox {SandboxId}",
            checkpoint.Id, type, id);
        return checkpoint;
    }

    public async Task<IReadOnlyList<SandboxCheckpoint>> ListCheckpointsAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(id, cancellationToken).ConfigureAwait(false);
        var backend = _backendSelector.Select(instance.Recipe);
        return await backend.ListCheckpointsAsync(instance.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid> RestoreCheckpointAsync(
        Guid id,
        Guid checkpointId,
        CancellationToken cancellationToken = default)
    {
        var instance = await GetInstanceAsync(id, cancellationToken).ConfigureAwait(false);
        var backend = _backendSelector.Select(instance.Recipe);

        var checkpoints = await backend.ListCheckpointsAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        var checkpoint = checkpoints.FirstOrDefault(c => c.Id == checkpointId)
            ?? throw new SandboxNotFoundException($"Checkpoint '{checkpointId}' was not found.");

        var restoredId = await backend.RestoreCheckpointAsync(instance.Id, checkpoint, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Restored checkpoint {Checkpoint} as system {SystemId}", checkpointId, restoredId);
        return restoredId;
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
            var backend = _backendSelector.Select(instance.Recipe);
            await backend.ShutdownAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        }

        await _repository.DeleteAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Deleted sandbox {SandboxId}", id);
    }

    private async Task<SandboxInstance> GetInstanceAsync(Guid id, CancellationToken cancellationToken)
    {
        var instance = await _repository.GetAsync(new SandboxInstanceId(id), cancellationToken).ConfigureAwait(false);
        return instance ?? throw new SandboxNotFoundException($"Sandbox '{id}' was not found.");
    }

    private Task DispatchAsync(SandboxInstance instance, CancellationToken cancellationToken)
    {
        var events = instance.DomainEvents.ToArray();
        instance.ClearDomainEvents();
        return _eventDispatcher.DispatchAsync(events, cancellationToken);
    }
}
