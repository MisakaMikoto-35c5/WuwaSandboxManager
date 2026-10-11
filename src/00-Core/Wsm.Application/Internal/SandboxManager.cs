using Microsoft.Extensions.Logging;
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
    private readonly ILogger<SandboxManager> _logger;

    public SandboxManager(
        ISandboxInstanceRepository repository,
        ISandboxEngineSelector selector,
        ISessionLauncher sessionLauncher,
        ILogger<SandboxManager> logger)
    {
        _repository = repository;
        _selector = selector;
        _sessionLauncher = sessionLauncher;
        _logger = logger;
    }

    public async Task<SandboxInstanceDto> CreateAsync(string? name, SandboxRecipe recipe, SandboxEngineKind? engine = null, CancellationToken cancellationToken = default)
    {
        var instance = SandboxInstance.Create(
            name is null ? null : new SandboxName(name),
            recipe,
            engine: engine);

        await PersistAsync(instance, cancellationToken).ConfigureAwait(false);
        return ToDto(instance);
    }

    public async Task<IReadOnlyList<SandboxInstanceDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var instances = await _repository.ListAsync(cancellationToken).ConfigureAwait(false);
        return instances.Select(ToDto).ToArray();
    }

    public async Task<SandboxInstanceDto> ImportAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        var instance = await _repository.ImportAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        return ToDto(instance);
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

        // Best effort: ask the engine to reclaim its on-disk data (HCS container
        // storage and snapshots). A cleanup failure -- including an engine that
        // does not support data deletion -- must never fail the delete itself.
        try
        {
            var engine = _selector.Select(RequestFor(instance));
            await engine.DeleteDataAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Intentionally swallowed: deletion of the record must still proceed.
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
            PreferredEngine = instance.Engine,
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
                await PersistAsync(instance, cancellationToken).ConfigureAwait(false);
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
            await PersistAsync(instance, cancellationToken).ConfigureAwait(false);
            throw;
        }

        await PersistAsync(instance, cancellationToken).ConfigureAwait(false);
        return ToDto(instance);
    }

    public async Task StopAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);

        var engine = _selector.Select(RequestFor(instance));
        await engine.StopAsync(instance.Id, cancellationToken).ConfigureAwait(false);

        instance.MarkStopped();
        await PersistAsync(instance, cancellationToken).ConfigureAwait(false);
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

    public async Task ConnectAsync(Guid id, SandboxConnectionMode mode, string? remoteAppProgram = null, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        var engine = _selector.Select(RequestFor(instance));

        var runtime = instance.Runtime
            ?? await engine.GetRuntimeAsync(instance.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new SandboxDomainException($"Sandbox '{id}' is not running; start it before connecting.");

        if (instance.Runtime is null)
        {
            instance.AttachRuntime(runtime);
            await PersistAsync(instance, cancellationToken).ConfigureAwait(false);
        }

        // Force-disconnect any session left over from a previous connect: the WDAG
        // guest keeps the disconnected session around and then refuses a new one
        // with "too many sessions established", so reclaim the slot first.
        await ReclaimGuestSessionsAsync(engine, instance.Id, cancellationToken).ConfigureAwait(false);

        await _sessionLauncher
            .LaunchAsync(instance.Id, runtime, mode, engine.Kind, remoteAppProgram, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Force-disconnects any orphaned (disconnected) guest session a previous
    /// connect left behind before a new one is established. The WDAG guest can
    /// keep a disconnected session and then refuse the next connection with "too
    /// many sessions established" (<c>ERROR_REMOTE_SESSION_LIMIT_EXCEEDED</c>),
    /// so those sessions are logged off first. A session that is still connected
    /// is left alone: it is the caller's own (or a concurrent connect) and the
    /// guest already replaces it. Best effort: engines that cannot run guest
    /// commands are skipped, and a failed reclaim must never block connecting.
    /// </summary>
    private async Task ReclaimGuestSessionsAsync(ISandboxEngine engine, SandboxInstanceId id, CancellationToken cancellationToken)
    {
        try
        {
            var table = await engine
                .CaptureAsync(id, new LogonCommand(QwinstaCommand), cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(table.StandardOutput))
            {
                // The engine cannot capture the guest's streams, so the session
                // table is unknown: log off the whole scanned id range and let the
                // guest ignore the ids that are absent (output suppressed).
                await engine
                    .ExecuteAsync(id, new LogonCommand(QuietLogoffRangeCommand), cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var orphans = GuestSessionTable.Parse(table.StandardOutput)
                .Where(session => !session.IsConnected)
                .Select(session => session.Id)
                .ToArray();

            if (orphans.Length == 0)
            {
                return;
            }

            _logger.LogInformation(
                "Force-disconnecting {Count} orphaned guest session(s) in sandbox {SandboxId}: {Sessions}.",
                orphans.Length, id, string.Join(", ", orphans));

            foreach (var sessionId in orphans)
            {
                await engine
                    .ExecuteAsync(id, new LogonCommand($"logoff {sessionId}"), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (SandboxNotSupportedException)
        {
            // The engine cannot run guest commands; there is nothing to reclaim.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reclaiming guest sessions for sandbox {SandboxId} failed.", id);
        }
    }

    /// <summary>Guest command that lists the current sessions.</summary>
    private const string QwinstaCommand = "qwinsta";

    /// <summary>
    /// Blind fallback for engines that cannot capture output: log off every low
    /// session id, discarding the "Session ID n not found" lines for the ids that
    /// do not exist.
    /// </summary>
    private const string QuietLogoffRangeCommand = "cmd.exe /c for /l %i in (1,1,20) do @logoff %i >nul 2>&1";

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
        await PersistAsync(instance, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Persists the aggregate as its single portable <c>.wsm</c> file.</summary>
    private async Task PersistAsync(SandboxInstance instance, CancellationToken cancellationToken)
    {
        await _repository.SaveAsync(instance, cancellationToken).ConfigureAwait(false);
    }

    private static SandboxStartRequest RequestFor(SandboxInstance instance) => new()
    {
        Id = instance.Id,
        Recipe = instance.Recipe,
        PreferredEngine = instance.Engine,
    };

    private static void EnsureCheckpointsSupported(ISandboxEngine engine)
    {
        if (!engine.Capabilities.SupportsCheckpoints)
        {
            throw new SandboxNotSupportedException(
                $"The engine '{engine.Name}' does not support checkpoints.");
        }
    }

    private SandboxInstanceDto ToDto(SandboxInstance instance)
        => new(
            instance.Id.Value,
            instance.Name.Value,
            instance.State,
            instance.Recipe,
            instance.Runtime,
            instance.FaultReason,
            instance.CreatedUtc,
            instance.UpdatedUtc,
            instance.Engine,
            _repository.GetManifestPath(instance.Id));
}
