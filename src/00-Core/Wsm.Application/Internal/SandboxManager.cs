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
        await ReconcileLiveStatesAsync(instances, cancellationToken).ConfigureAwait(false);
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
        await ReconcileLiveStatesAsync(new[] { instance }, cancellationToken).ConfigureAwait(false);
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

        // Ask the engine to reclaim its on-disk data (HCS container storage and
        // snapshots). An engine that does not support data deletion at all is
        // tolerated; a genuine cleanup failure (e.g. the storage is still locked)
        // is surfaced so the delete does not silently leave the data behind.
        try
        {
            var engine = _selector.Select(RequestFor(instance));
            await engine.DeleteDataAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (SandboxNotSupportedException)
        {
            // The engine has no data lifecycle; the record delete still proceeds.
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

    public async Task<SandboxRuntimeInfo?> GetRuntimeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        var engine = _selector.Select(RequestFor(instance));
        return await ResolveRuntimeAsync(instance, engine, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SandboxRuntimeInfo> PrepareConnectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var (_, runtime, _) = await PrepareConnectCoreAsync(
            id, replaceActiveSessions: false, ensureRemoteAppAllowed: false, cancellationToken).ConfigureAwait(false);
        return runtime;
    }

    public async Task ConnectAsync(Guid id, SandboxConnectionMode mode, string? remoteAppProgram = null, string? remoteAppArguments = null, CancellationToken cancellationToken = default)
    {
        // A RemoteApp connect replaces whatever session the guest currently holds:
        // the single-session guest refuses a RAIL logon while another session is
        // connected ("too many sessions"), so the existing session must go first.
        // It also opens the guest's RemoteApp surface before the client connects.
        var remoteApp = mode == SandboxConnectionMode.RemoteApp;
        var (instanceId, runtime, engine) = await PrepareConnectCoreAsync(
            id, replaceActiveSessions: remoteApp, ensureRemoteAppAllowed: remoteApp, cancellationToken).ConfigureAwait(false);

        await _sessionLauncher
            .LaunchAsync(instanceId, runtime, mode, engine.Kind, remoteAppProgram, remoteAppArguments, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Shared pre-connect sequence used by both <see cref="ConnectAsync"/> and
    /// <see cref="PrepareConnectAsync"/>: resolves the instance's live runtime
    /// (from the aggregate, falling back to the engine), attaches and persists it
    /// when it was discovered through the engine, then reclaims any orphaned guest
    /// sessions so the guest accepts the next connect. Returns the instance id,
    /// the resolved runtime and the engine that owns it, so callers can launch
    /// the session.
    /// </summary>
    /// <param name="replaceActiveSessions">
    /// When true, even a still-connected session is reclaimed. Required for RAIL
    /// connects: the single-session guest refuses a RemoteApp logon while another
    /// session exists instead of arbitrating it.
    /// </param>
    /// <param name="ensureRemoteAppAllowed">
    /// When true (RAIL connects), the guest's RemoteApp surface is opened before
    /// the client connects — see <see cref="EnsureGuestRemoteAppAllowedAsync"/>.
    /// </param>
    /// <exception cref="SandboxDomainException">
    /// The instance is not running (no cached runtime and the engine reports none).
    /// </exception>
    private async Task<(SandboxInstanceId Id, SandboxRuntimeInfo Runtime, ISandboxEngine Engine)> PrepareConnectCoreAsync(
        Guid id,
        bool replaceActiveSessions,
        bool ensureRemoteAppAllowed,
        CancellationToken cancellationToken)
    {
        var instance = await GetRequiredAsync(id, cancellationToken).ConfigureAwait(false);
        var engine = _selector.Select(RequestFor(instance));

        var runtime = await ResolveRuntimeAsync(instance, engine, cancellationToken).ConfigureAwait(false)
            ?? throw new SandboxDomainException($"Sandbox '{id}' is not running; start it before connecting.");

        if (instance.Runtime is null)
        {
            instance.AttachRuntime(runtime);
            await PersistAsync(instance, cancellationToken).ConfigureAwait(false);
        }

        // Force-disconnect any session left over from a previous connect: the WDAG
        // guest keeps the disconnected session around and then refuses a new one
        // with "too many sessions established", so reclaim the slot first.
        await ReclaimGuestSessionsAsync(engine, instance.Id, replaceActiveSessions, cancellationToken).ConfigureAwait(false);

        // Open the guest's RemoteApp surface so a RAIL connect can actually
        // launch the requested program (RemoteApp-only for the guest).
        if (ensureRemoteAppAllowed)
        {
            await EnsureGuestRemoteAppAllowedAsync(engine, instance.Id, cancellationToken).ConfigureAwait(false);
        }

        return (instance.Id, runtime, engine);
    }

    /// <summary>
    /// Reconciles freshly loaded instances with the engines' live state. The
    /// persisted state is deliberately transient (a loaded sandbox rehydrates
    /// as <see cref="SandboxState.Created"/>), so a sandbox that is actually
    /// running — started by another process, or still up from before an app
    /// restart — would otherwise be reported as not-running, which hides
    /// Connect/Disconnect/Stop in the UI. Instances this process already knows
    /// are live are left alone. Best effort: an engine that cannot enumerate
    /// (not elevated, unavailable) leaves the persisted state untouched.
    /// </summary>
    private async Task ReconcileLiveStatesAsync(
        IReadOnlyList<SandboxInstance> instances,
        CancellationToken cancellationToken)
    {
        var stale = instances.Where(instance => !instance.State.IsLive()).ToArray();
        if (stale.Length == 0)
        {
            return;
        }

        var enumerateCache = new Dictionary<ISandboxEngine, IReadOnlyList<SandboxInstanceId>>();
        foreach (var instance in stale)
        {
            try
            {
                var engine = _selector.Select(RequestFor(instance));
                if (!enumerateCache.TryGetValue(engine, out var running))
                {
                    running = await engine.EnumerateAsync(cancellationToken).ConfigureAwait(false);
                    enumerateCache[engine] = running;
                }

                if (!running.Contains(instance.Id))
                {
                    continue;
                }

                var runtime = await engine.GetRuntimeAsync(instance.Id, cancellationToken).ConfigureAwait(false);
                runtime = EnforceRecipeTransport(instance, engine, runtime);
                if (runtime is not null)
                {
                    instance.MarkRunning(runtime);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(
                    ex,
                    "Reconciling the live state of sandbox {SandboxId} failed; keeping the persisted state.",
                    instance.Id);
            }
        }
    }

    /// <summary>
    /// Resolves an instance's live runtime: the aggregate's cached runtime when it
    /// has one, otherwise the engine's current runtime. Returns <c>null</c> when
    /// the engine reports the instance is not running.
    /// </summary>
    private static async Task<SandboxRuntimeInfo?> ResolveRuntimeAsync(
        SandboxInstance instance,
        ISandboxEngine engine,
        CancellationToken cancellationToken)
    {
        var runtime = instance.Runtime
            ?? await engine.GetRuntimeAsync(instance.Id, cancellationToken).ConfigureAwait(false);
        return EnforceRecipeTransport(instance, engine, runtime);
    }

    /// <summary>
    /// The recipe's transport is authoritative: the named pipe is the default and
    /// TCP is opt-in (an HCS-sandbox feature). A probe that finds a live 3389
    /// listener must not silently turn a pipe sandbox onto TCP — the guest's
    /// registry persists on the user-data disk, so a sandbox that once ran TCP
    /// keeps listening even after it was switched back to the pipe. Symmetrically,
    /// a TCP sandbox whose listener is unreachable fails loudly instead of falling
    /// back to the arbitrated pipe (which would re-introduce the host-wide
    /// session kicking the user switched away from).
    /// </summary>
    /// <exception cref="SandboxBackendException">
    /// TCP was requested but the guest listener is not reachable.
    /// </exception>
    private static SandboxRuntimeInfo? EnforceRecipeTransport(
        SandboxInstance instance,
        ISandboxEngine engine,
        SandboxRuntimeInfo? runtime)
    {
        if (runtime is null || engine.Kind != SandboxEngineKind.HcsSandbox)
        {
            return runtime;
        }

        if (instance.Recipe.Transport != RdpTransportKind.Tcp)
        {
            return runtime.Transport == RdpTransportKind.Tcp
                ? runtime with
                {
                    Transport = RdpTransportKind.NamedPipe,
                    IpAddress = string.Empty,
                    IpAddresses = Array.Empty<string>(),
                }
                : runtime;
        }

        if (runtime.Transport != RdpTransportKind.Tcp)
        {
            throw new SandboxBackendException(
                $"The TCP RDP transport was requested for sandbox {instance.Id}, but the guest " +
                "listener on 3389 is not reachable. Start the sandbox again, or switch it back " +
                "to the named-pipe transport.",
                0);
        }

        return runtime;
    }

    /// <summary>
    /// Force-disconnects any orphaned (disconnected) guest session a previous
    /// connect left behind before a new one is established. The WDAG guest
    /// can keep a disconnected session and then refuse the next connection with
    /// "too many sessions established" (<c>ERROR_REMOTE_SESSION_LIMIT_EXCEEDED</c>),
    /// so those sessions are logged off first. When
    /// <paramref name="replaceActiveSessions"/> is set (a RemoteApp connect), even
    /// a still-connected session is logged off: the guest does not arbitrate a
    /// RAIL logon against an existing session, it refuses it.
    /// <para>
    /// The guest tears a logged-off session down asynchronously — the session can
    /// linger for tens of seconds after <c>logoff</c> returns — so the reclaim
    /// then polls the session table until the reclaimed sessions are actually gone
    /// (bounded by <see cref="SessionReclaimTimeout"/>); connecting while the slot
    /// is still occupied is what surfaces as "too many sessions". A session that
    /// is still connected and is not being replaced is left alone: it is the
    /// caller's own (or a concurrent connect) and the guest arbitrates it. Best
    /// effort overall: engines that cannot run guest commands are skipped, and a
    /// failed reclaim must never block connecting.
    /// </para>
    /// </summary>
    private async Task ReclaimGuestSessionsAsync(
        ISandboxEngine engine,
        SandboxInstanceId id,
        bool replaceActiveSessions,
        CancellationToken cancellationToken)
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
                .Where(session => replaceActiveSessions || !session.IsConnected)
                .Select(session => session.Id)
                .ToArray();

            if (orphans.Length == 0)
            {
                return;
            }

            _logger.LogInformation(
                "Force-disconnecting {Count} guest session(s) in sandbox {SandboxId}: {Sessions}.",
                orphans.Length, id, string.Join(", ", orphans));

            foreach (var sessionId in orphans)
            {
                await engine
                    .ExecuteAsync(id, new LogonCommand($"logoff {sessionId}"), cancellationToken)
                    .ConfigureAwait(false);
            }

            await WaitForSessionsGoneAsync(engine, id, orphans, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Polls the guest session table until every id in <paramref name="sessionIds"/>
    /// has disappeared. The guest's teardown is asynchronous and slow (observed up
    /// to ~50s), so the wait is bounded: when <see cref="SessionReclaimTimeout"/>
    /// elapses the connect proceeds anyway rather than being blocked forever.
    /// </summary>
    private async Task WaitForSessionsGoneAsync(
        ISandboxEngine engine,
        SandboxInstanceId id,
        IReadOnlyCollection<int> sessionIds,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + SessionReclaimTimeout;

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(SessionReclaimPollInterval, cancellationToken).ConfigureAwait(false);

            var table = await engine
                .CaptureAsync(id, new LogonCommand(QwinstaCommand), cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(table.StandardOutput))
            {
                // Transient capture failure: keep waiting until the deadline.
                continue;
            }

            var remaining = GuestSessionTable.Parse(table.StandardOutput)
                .Count(session => sessionIds.Contains(session.Id));
            if (remaining == 0)
            {
                return;
            }
        }

        _logger.LogWarning(
            "Guest session(s) {Sessions} in sandbox {SandboxId} still present {Timeout}s after logoff; connecting anyway.",
            string.Join(", ", sessionIds), id, SessionReclaimTimeout.TotalSeconds);
    }

    /// <summary>Guest command that lists the current sessions.</summary>
    private const string QwinstaCommand = "qwinsta";

    /// <summary>
    /// Blind fallback for engines that cannot capture output: log off every low
    /// session id, discarding the "Session ID n not found" lines for the ids that
    /// do not exist.
    /// </summary>
    private const string QuietLogoffRangeCommand = "cmd.exe /c for /l %i in (1,1,20) do @logoff %i >nul 2>&1";

    /// <summary>
    /// Best-effort opening of the guest's RemoteApp surface
    /// (<see cref="RemoteAppGuestPolicy.EnablementCommands"/>): engines that
    /// cannot run guest commands are skipped, and a failure only logs — the
    /// connect proceeds and the client reports whatever the guest decides.
    /// </summary>
    private async Task EnsureGuestRemoteAppAllowedAsync(
        ISandboxEngine engine,
        SandboxInstanceId id,
        CancellationToken cancellationToken)
    {
        try
        {
            var exitCodes = new List<int>(RemoteAppGuestPolicy.EnablementCommands.Count);
            foreach (var command in RemoteAppGuestPolicy.EnablementCommands)
            {
                exitCodes.Add(await engine
                    .ExecuteAsync(id, new LogonCommand(command), cancellationToken)
                    .ConfigureAwait(false));
            }

            _logger.LogInformation(
                "Guest RemoteApp surface prepared for sandbox {SandboxId} (exit codes {ExitCodes}).",
                id, string.Join("/", exitCodes));

            if (exitCodes.Exists(code => code != 0))
            {
                _logger.LogWarning(
                    "Preparing the guest RemoteApp surface for sandbox {SandboxId} reported exit codes {ExitCodes}; " +
                    "a RemoteApp connect may stall while the guest refuses the program.",
                    id, string.Join("/", exitCodes));
            }
        }
        catch (SandboxNotSupportedException ex)
        {
            // The engine cannot run guest commands; there is nothing to prepare.
            _logger.LogInformation(
                "Skipping the guest RemoteApp preparation for sandbox {SandboxId}: {Reason}", id, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Preparing the guest RemoteApp surface for sandbox {SandboxId} failed.", id);
        }
    }

    /// <summary>
    /// Bound for waiting out the guest's asynchronous session teardown after a
    /// <c>logoff</c>. Settable from tests.
    /// </summary>
    internal TimeSpan SessionReclaimTimeout { get; set; } = TimeSpan.FromSeconds(75);

    /// <summary>Interval between session-table polls while reclaiming. Settable from tests.</summary>
    internal TimeSpan SessionReclaimPollInterval { get; set; } = TimeSpan.FromMilliseconds(1500);

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
