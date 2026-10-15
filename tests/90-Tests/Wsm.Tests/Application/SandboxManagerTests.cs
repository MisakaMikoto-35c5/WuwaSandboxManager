using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Domain;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Application;
using Wsm.Application.EngineSelection.Internal;
using Wsm.Application.Internal;
using Wsm.Connection.Session;
using Wsm.Engines.Abstractions;
using Wsm.Engines.Abstractions.Internal;
using Wsm.Persistence;
using Xunit;

namespace Wsm.Tests.Application;

/// <summary>
/// Covers <see cref="SandboxManager"/> lifecycle orchestration and the
/// cross-engine checkpoint path against a stub engine and an in-memory
/// repository.
/// </summary>
public sealed class SandboxManagerTests
{
    [Fact]
    public async Task Create_then_start_returns_running_dto()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var manager = Manager(repository, engine);

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        Assert.Equal(SandboxState.Created, created.State);
        Assert.NotNull(created.ManifestPath);

        var started = await manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop);

        Assert.Equal(SandboxState.Running, started.State);
        Assert.NotNull(started.Runtime);
        Assert.Equal(created.Id, started.Runtime!.SandboxId.Value);
    }

    [Fact]
    public async Task Create_records_the_requested_engine()
    {
        var manager = Manager(new InMemoryRepository(), new StubEngine());

        var pinned = await manager.CreateAsync("Box", SandboxRecipe.Default, SandboxEngineKind.WindowsSandbox);
        Assert.Equal(SandboxEngineKind.WindowsSandbox, pinned.Engine);

        var auto = await manager.CreateAsync("Auto", SandboxRecipe.Default);
        Assert.Null(auto.Engine);
    }

    [Fact]
    public async Task Start_passes_the_instance_engine_as_the_preferred_engine()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var manager = Manager(repository, engine);

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default, SandboxEngineKind.WindowsSandbox);

        var started = await manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop);

        // The stub is the only registered engine, so the selector falls back to
        // it -- but the request must still carry the instance's preference.
        Assert.NotNull(engine.LastStartRequest);
        Assert.Equal(SandboxEngineKind.WindowsSandbox, engine.LastStartRequest!.PreferredEngine);
        Assert.Equal(SandboxEngineKind.WindowsSandbox, started.Engine);
    }

    [Fact]
    public async Task Start_leaves_the_preference_null_for_automatic_selection()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var manager = Manager(repository, engine);

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        await manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop);

        Assert.NotNull(engine.LastStartRequest);
        Assert.Null(engine.LastStartRequest!.PreferredEngine);
    }

    [Fact]
    public async Task Stop_transitions_to_stopped()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var manager = Manager(repository, engine);

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        await manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop);

        await manager.StopAsync(created.Id);

        var reloaded = Assert.Single(await manager.ListAsync());
        Assert.Equal(SandboxState.Stopped, reloaded.State);
        Assert.Null(reloaded.Runtime);
    }

    [Fact]
    public async Task Checkpoints_route_to_selected_engine()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var manager = Manager(repository, engine);

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);

        var saved = await manager.SaveCheckpointAsync(created.Id, "cp1", SandboxCheckpointType.FullState);
        Assert.Equal("cp1", saved.Name);
        Assert.Equal(SandboxCheckpointType.FullState, saved.Type);
        Assert.Equal(1, engine.SaveCount);

        var listed = await manager.ListCheckpointsAsync(created.Id);
        Assert.Single(listed);
        Assert.Equal(1, engine.ListCount);
    }

    [Fact]
    public async Task Restore_checkpoint_resolves_the_checkpoint_and_routes_to_engine()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var manager = Manager(repository, engine);

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        var saved = await manager.SaveCheckpointAsync(created.Id, "cp1", SandboxCheckpointType.FullState);

        var restored = await manager.RestoreCheckpointAsync(created.Id, saved.Id);

        Assert.Equal(engine.RestoredId, restored);
        Assert.Equal(saved, engine.LastRestored);
    }

    [Fact]
    public async Task Start_faults_the_instance_and_propagates_when_engine_throws()
    {
        var repository = new InMemoryRepository();
        var manager = Manager(repository, new ThrowingStartEngine());

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop));

        var persisted = await repository.GetAsync(new SandboxInstanceId(created.Id));
        Assert.NotNull(persisted);
        Assert.Equal(SandboxState.Faulted, persisted!.State);
        Assert.Equal("boom", persisted.FaultReason);
    }

    [Fact]
    public async Task Start_rejects_a_second_instance_when_engine_is_single_instance()
    {
        var repository = new InMemoryRepository();
        var other = SandboxInstanceId.New();
        var manager = Manager(repository, new SingleInstanceEngine(other));

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);

        var ex = await Assert.ThrowsAsync<MultipleInstancesNotAllowedException>(
            () => manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop));

        Assert.Contains(other.Value.ToString(), ex.Message);

        var persisted = await repository.GetAsync(new SandboxInstanceId(created.Id));
        Assert.NotNull(persisted);
        Assert.Equal(SandboxState.Faulted, persisted!.State);
    }

    [Fact]
    public async Task List_reconciles_a_running_instance_the_process_did_not_start()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var manager = Manager(repository, engine);

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);

        // Simulate an app restart: the repository rehydrates the instance as
        // Created while the engine still hosts the VM. List must reconcile
        // with the engine or the UI hides Connect/Disconnect/Stop.
        engine.RunningToReport.Add(SandboxInstanceId.From(created.Id));
        engine.RuntimeToReport = new SandboxRuntimeInfo
        {
            SandboxId = SandboxInstanceId.From(created.Id),
            VmId = created.Id,
            Transport = RdpTransportKind.NamedPipe,
        };

        var listed = Assert.Single(await manager.ListAsync());

        Assert.Equal(SandboxState.Running, listed.State);
        Assert.NotNull(listed.Runtime);
    }

    [Fact]
    public async Task Get_keeps_the_persisted_state_when_the_engine_reports_nothing()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var manager = Manager(repository, engine);

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);

        var fetched = await manager.GetAsync(created.Id);

        Assert.Equal(SandboxState.Created, fetched.State);
        Assert.Null(fetched.Runtime);
    }

    [Fact]
    public async Task Get_round_trips_a_created_instance()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var manager = Manager(repository, engine);

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);

        var fetched = await manager.GetAsync(created.Id);

        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal("Box", fetched.Name);
        Assert.Equal(SandboxState.Created, fetched.State);
        Assert.NotEqual(default, fetched.CreatedUtc);
    }

    [Fact]
    public async Task Get_throws_when_the_instance_is_missing()
    {
        var manager = Manager(new InMemoryRepository(), new StubEngine());

        await Assert.ThrowsAsync<SandboxNotFoundException>(() => manager.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Delete_removes_the_instance()
    {
        var repository = new InMemoryRepository();
        var manager = Manager(repository, new StubEngine());

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        await manager.DeleteAsync(created.Id);

        Assert.Empty(await manager.ListAsync());
    }

    [Fact]
    public async Task Delete_requests_engine_data_cleanup()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var manager = Manager(repository, engine);

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        await manager.DeleteAsync(created.Id);

        Assert.Contains(new SandboxInstanceId(created.Id), engine.DeletedData);
        Assert.Empty(await manager.ListAsync());
    }

    [Fact]
    public async Task Delete_succeeds_when_the_engine_does_not_support_data_cleanup()
    {
        var repository = new InMemoryRepository();
        // ThrowingStartEngine leaves DeleteDataAsync at the default-throwing
        // implementation, as the Windows engines do.
        var manager = Manager(repository, new ThrowingStartEngine());

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);

        await manager.DeleteAsync(created.Id);

        Assert.Empty(await manager.ListAsync());
    }

    [Fact]
    public async Task Delete_surfaces_a_data_cleanup_failure_and_keeps_the_record()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine { ThrowOnDeleteData = true };
        var manager = Manager(repository, engine);

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);

        // A genuine cleanup failure (e.g. the storage is still locked) must
        // reach the caller instead of silently leaving the data behind; the
        // record is kept so the delete can be retried.
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.DeleteAsync(created.Id));

        Assert.NotEmpty(await manager.ListAsync());
    }

    [Fact]
    public async Task Get_capabilities_reports_the_selected_engine()
    {
        var engine = new StubEngine();
        var manager = Manager(new InMemoryRepository(), engine);

        var capabilities = await manager.GetCapabilitiesAsync();

        Assert.Equal(engine.Name, capabilities.BackendName);
        Assert.Equal(engine.Kind, capabilities.Kind);
        Assert.True(capabilities.IsAvailable);
        Assert.True(capabilities.Capabilities.SupportsCheckpoints);
    }

    [Fact]
    public async Task Connect_routes_the_connection_with_the_selected_engine_kind()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var launcher = new StubSessionLauncher();
        var manager = Manager(repository, engine, launcher);

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        await manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop);

        await manager.ConnectAsync(created.Id, SandboxConnectionMode.FullDesktop);

        Assert.Equal(created.Id, launcher.LastId!.Value.Value);
        Assert.Equal(SandboxConnectionMode.FullDesktop, launcher.LastMode);
        Assert.Equal(engine.Kind, launcher.LastEngineKind);
    }

    [Fact]
    public async Task Connect_passes_the_rail_program_override_to_the_session_launcher()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var launcher = new StubSessionLauncher();
        var manager = Manager(repository, engine, launcher);

        var recipe = SandboxRecipe.Default with { RailMode = true, RailProgram = @"C:\Windows\regedit.exe" };
        var created = await manager.CreateAsync("Box", recipe);
        await manager.StartAsync(created.Id, SandboxConnectionMode.RemoteApp);

        await manager.ConnectAsync(created.Id, SandboxConnectionMode.RemoteApp, @"C:\Windows\System32\cmd.exe");

        Assert.Equal(SandboxConnectionMode.RemoteApp, launcher.LastMode);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", launcher.LastRemoteAppProgram);
    }

    [Fact]
    public void Recipe_resolves_the_default_rail_program_when_unset()
    {
        Assert.Equal(SandboxRecipe.DefaultRailProgram, SandboxRecipe.Default.ResolvedRailProgram);
        Assert.Equal(
            @"C:\Windows\regedit.exe",
            (SandboxRecipe.Default with { RailProgram = @"C:\Windows\regedit.exe" }).ResolvedRailProgram);
    }

    [Fact]
    public async Task Connect_force_disconnects_an_orphaned_guest_session_before_launching()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var launcher = new StubSessionLauncher();
        var manager = Manager(repository, engine, launcher);
        manager.SessionReclaimPollInterval = TimeSpan.FromMilliseconds(10);

        // The session query reports a disconnected ("Disc") leftover session (id
        // 2); the reclaim must log it off before the session launcher is invoked.
        // The second table (session gone) answers the post-logoff poll.
        engine.QwinstaTables.Enqueue(
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            "                                                     2  Disc\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n");
        engine.QwinstaTables.Enqueue(
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n");

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        await manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop);

        await manager.ConnectAsync(created.Id, SandboxConnectionMode.FullDesktop);

        Assert.Contains("logoff 2", engine.GuestCommands);
        Assert.Equal(created.Id, launcher.LastId!.Value.Value);
    }

    [Fact]
    public async Task Connect_waits_until_the_orphaned_session_is_gone_before_launching()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var launcher = new StubSessionLauncher();
        var manager = Manager(repository, engine, launcher);
        manager.SessionReclaimPollInterval = TimeSpan.FromMilliseconds(10);

        // The orphan lingers through the first post-logoff poll and disappears by
        // the second; the launcher must only run after the table is clear.
        engine.QwinstaTables.Enqueue(
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            "                                                     2  Disc\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n");
        engine.QwinstaTables.Enqueue(
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            "                                                     2  Disc\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n");
        engine.QwinstaTables.Enqueue(
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n");

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        await manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop);

        await manager.ConnectAsync(created.Id, SandboxConnectionMode.FullDesktop);

        Assert.Contains("logoff 2", engine.GuestCommands);
        Assert.Equal(3, engine.QwinstaQueryCount);
        Assert.Equal(created.Id, launcher.LastId!.Value.Value);
    }

    [Fact]
    public async Task RemoteApp_connect_logs_off_the_still_connected_session()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var launcher = new StubSessionLauncher();
        var manager = Manager(repository, engine, launcher);
        manager.SessionReclaimPollInterval = TimeSpan.FromMilliseconds(10);

        // A full-desktop session is still Active; the single-session guest refuses
        // a RAIL logon while it exists, so a RemoteApp connect must replace it.
        // The second table (session gone) answers the post-logoff poll.
        engine.QwinstaTables.Enqueue(
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            " 31c5ce94259d4006a9e4#0   WDAGUtilityAccount         1  Active\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n");
        engine.QwinstaTables.Enqueue(
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n");

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        await manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop);

        await manager.ConnectAsync(created.Id, SandboxConnectionMode.RemoteApp, @"C:\Windows\regedit.exe");

        Assert.Contains("logoff 1", engine.GuestCommands);
        Assert.Equal(created.Id, launcher.LastId!.Value.Value);
    }

    [Fact]
    public async Task Connect_proceeds_when_the_reclaim_wait_times_out()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var launcher = new StubSessionLauncher();
        var manager = Manager(repository, engine, launcher);
        manager.SessionReclaimPollInterval = TimeSpan.FromMilliseconds(10);
        manager.SessionReclaimTimeout = TimeSpan.FromMilliseconds(50);

        // The orphan never disappears (the stub repeats the last table): the wait
        // is best effort and the connect must still proceed after the timeout.
        engine.QwinstaTables.Enqueue(
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            "                                                     2  Disc\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n");

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        await manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop);

        await manager.ConnectAsync(created.Id, SandboxConnectionMode.FullDesktop);

        Assert.Contains("logoff 2", engine.GuestCommands);
        Assert.Equal(created.Id, launcher.LastId!.Value.Value);
    }

    [Fact]
    public async Task Connect_leaves_a_still_connected_guest_session_alone()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var launcher = new StubSessionLauncher();
        var manager = Manager(repository, engine, launcher);

        // A session that is still connected is the caller's own (or a concurrent
        // connect): it must not be force-disconnected.
        engine.QwinstaTables.Enqueue(
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            " 31c5ce94259d4006a9e4#0                              1  Conn\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n");

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        await manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop);

        await manager.ConnectAsync(created.Id, SandboxConnectionMode.FullDesktop);

        Assert.Empty(engine.GuestCommands);
        Assert.Equal(created.Id, launcher.LastId!.Value.Value);
    }

    [Fact]
    public async Task Connect_does_not_touch_guest_sessions_when_the_table_is_clear()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var launcher = new StubSessionLauncher();
        var manager = Manager(repository, engine, launcher);

        engine.QwinstaTables.Enqueue(
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n");

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);
        await manager.StartAsync(created.Id, SandboxConnectionMode.FullDesktop);

        await manager.ConnectAsync(created.Id, SandboxConnectionMode.FullDesktop);

        Assert.Empty(engine.GuestCommands);
        Assert.Equal(created.Id, launcher.LastId!.Value.Value);
    }

    [Fact]
    public async Task Connect_enforces_the_pipe_transport_even_when_the_engine_probes_tcp()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var launcher = new StubSessionLauncher();
        var manager = Manager(repository, engine, launcher);

        engine.QwinstaTables.Enqueue(
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n");

        var created = await manager.CreateAsync("Box", SandboxRecipe.Default);

        // A cross-process connect (nothing cached) probes the engine; a guest
        // that once ran TCP still listens on 3389 (the registry persists on the
        // user-data disk), so the probe reports Tcp. The recipe says pipe, and
        // the recipe is authoritative: the launch must use the pipe.
        engine.RuntimeToReport = new SandboxRuntimeInfo
        {
            SandboxId = SandboxInstanceId.From(created.Id),
            VmId = created.Id,
            Transport = RdpTransportKind.Tcp,
            IpAddress = "172.21.95.199",
            IpAddresses = ["172.21.95.199"],
        };

        await manager.ConnectAsync(created.Id, SandboxConnectionMode.FullDesktop);

        Assert.NotNull(launcher.LastRuntime);
        Assert.Equal(RdpTransportKind.NamedPipe, launcher.LastRuntime!.Transport);
        Assert.Equal(string.Empty, launcher.LastRuntime.IpAddress);
        Assert.Empty(launcher.LastRuntime.IpAddresses);
    }

    [Fact]
    public async Task Connect_with_tcp_transport_fails_when_the_guest_listener_is_unreachable()
    {
        var repository = new InMemoryRepository();
        var engine = new StubEngine();
        var launcher = new StubSessionLauncher();
        var manager = Manager(repository, engine, launcher);

        var created = await manager.CreateAsync(
            "Box", SandboxRecipe.Default with { Transport = RdpTransportKind.Tcp });

        // The cross-process probe finds no 3389 listener: TCP was explicitly
        // requested, so the connect must fail loudly instead of silently
        // landing on the arbitrated pipe (which would re-introduce the
        // host-wide session kicking the user switched away from).
        engine.RuntimeToReport = new SandboxRuntimeInfo
        {
            SandboxId = SandboxInstanceId.From(created.Id),
            VmId = created.Id,
            Transport = RdpTransportKind.NamedPipe,
        };

        await Assert.ThrowsAsync<SandboxBackendException>(
            () => manager.ConnectAsync(created.Id, SandboxConnectionMode.FullDesktop));

        Assert.Null(launcher.LastId);
    }

    private static SandboxManager Manager(ISandboxInstanceRepository repository, ISandboxEngine engine)
    {
        var selector = new EngineSelector(new SandboxEngineRegistry(new ISandboxEngine[] { engine }));
        return new SandboxManager(repository, selector, new StubSessionLauncher(), NullLogger<SandboxManager>.Instance);
    }

    private static SandboxManager Manager(
        ISandboxInstanceRepository repository,
        ISandboxEngine engine,
        ISessionLauncher sessionLauncher)
    {
        var selector = new EngineSelector(new SandboxEngineRegistry(new ISandboxEngine[] { engine }));
        return new SandboxManager(repository, selector, sessionLauncher, NullLogger<SandboxManager>.Instance);
    }

    private sealed class StubSessionLauncher : ISessionLauncher
    {
        public SandboxInstanceId? LastId { get; private set; }

        public SandboxConnectionMode? LastMode { get; private set; }

        public SandboxEngineKind? LastEngineKind { get; private set; }

        public string? LastRemoteAppProgram { get; private set; }

        /// <summary>The runtime the connect flow resolved and handed to the launch.</summary>
        public SandboxRuntimeInfo? LastRuntime { get; private set; }

        public Task LaunchAsync(
            SandboxInstanceId id,
            SandboxRuntimeInfo runtime,
            SandboxConnectionMode mode,
            SandboxEngineKind engineKind,
            string? remoteAppProgram = null,
            CancellationToken cancellationToken = default)
        {
            LastId = id;
            LastMode = mode;
            LastEngineKind = engineKind;
            LastRemoteAppProgram = remoteAppProgram;
            LastRuntime = runtime;
            return Task.CompletedTask;
        }

        public void CloseAll()
        {
        }
    }

    private sealed class InMemoryRepository : ISandboxInstanceRepository
    {
        private readonly Dictionary<SandboxInstanceId, SandboxInstance> _store = new();

        public Task SaveAsync(SandboxInstance instance, CancellationToken cancellationToken = default)
        {
            _store[instance.Id] = instance;
            return Task.CompletedTask;
        }

        public Task<SandboxInstance?> GetAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult(_store.TryGetValue(id, out var instance) ? instance : null);

        public Task<IReadOnlyList<SandboxInstance>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SandboxInstance>>(_store.Values.ToArray());

        public Task DeleteAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
        {
            _store.Remove(id);
            return Task.CompletedTask;
        }

        public string GetManifestPath(SandboxInstanceId id) => $"sandboxes/{id.Value:D}.wsm";

        public Task<SandboxInstance> ImportAsync(string manifestPath, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubEngine : ISandboxEngine
    {
        private readonly List<SandboxCheckpoint> _checkpoints = new();

        public int SaveCount { get; private set; }

        public int ListCount { get; private set; }

        public bool ThrowOnDeleteData { get; set; }

        public List<SandboxInstanceId> DeletedData { get; } = new();

        public Guid RestoredId { get; } = Guid.NewGuid();

        public SandboxCheckpoint? LastRestored { get; private set; }

        public SandboxStartRequest? LastStartRequest { get; private set; }

        /// <summary>Guest commands the reclaim executed (e.g. <c>logoff 1</c>).</summary>
        public List<string> GuestCommands { get; } = new();

        /// <summary>How many times the guest session table was queried.</summary>
        public int QwinstaQueryCount { get; private set; }

        /// <summary>
        /// Scripted <c>qwinsta</c> outputs. One entry is returned per query; the
        /// last entry repeats. An empty queue yields an empty table.
        /// </summary>
        public Queue<string> QwinstaTables { get; } = new();

        public string Name => "stub";

        public SandboxEngineKind Kind => SandboxEngineKind.HcsSandbox;

        public SandboxEngineCapabilities Capabilities => new() { SupportsCheckpoints = true };

        public bool IsAvailable => true;

        /// <summary>What <see cref="EnumerateAsync"/> reports as live (other processes' VMs).</summary>
        public List<SandboxInstanceId> RunningToReport { get; } = new();

        public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SandboxInstanceId>>(RunningToReport.ToArray());

        public Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default)
        {
            LastStartRequest = request;
            return Task.FromResult(new SandboxRuntimeInfo
            {
                SandboxId = request.Id,
                VmId = request.Id.Value,
                Transport = RdpTransportKind.NamedPipe,
            });
        }

        public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        /// <summary>What the cross-process probe (<see cref="GetRuntimeAsync"/>) reports.</summary>
        public SandboxRuntimeInfo? RuntimeToReport { get; set; }

        public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult(RuntimeToReport);

        public Task<SandboxCheckpoint> SaveCheckpointAsync(
            SandboxInstanceId id,
            string name,
            SandboxCheckpointType type,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            var checkpoint = new SandboxCheckpoint(Guid.NewGuid(), name, type, "state.vmrs", DateTimeOffset.UtcNow);
            _checkpoints.Add(checkpoint);
            return Task.FromResult(checkpoint);
        }

        public Task<IReadOnlyList<SandboxCheckpoint>> ListCheckpointsAsync(
            SandboxInstanceId id,
            CancellationToken cancellationToken = default)
        {
            ListCount++;
            return Task.FromResult<IReadOnlyList<SandboxCheckpoint>>(_checkpoints.ToArray());
        }

        public Task<Guid> RestoreCheckpointAsync(
            SandboxInstanceId id,
            SandboxCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
        {
            LastRestored = checkpoint;
            return Task.FromResult(RestoredId);
        }

        public Task DeleteDataAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
        {
            if (ThrowOnDeleteData)
            {
                throw new InvalidOperationException("cleanup failed");
            }

            DeletedData.Add(id);
            return Task.CompletedTask;
        }

        public async Task<GuestCommandResult> CaptureAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default)
        {
            if (command.Command == "qwinsta")
            {
                QwinstaQueryCount++;
                var table = QwinstaTables.Count == 0
                    ? string.Empty
                    : QwinstaTables.Count == 1 ? QwinstaTables.Peek() : QwinstaTables.Dequeue();
                return new GuestCommandResult(0, table, string.Empty);
            }

            var exitCode = await ExecuteAsync(id, command, cancellationToken).ConfigureAwait(false);
            return GuestCommandResult.FromExitCode(exitCode);
        }

        public Task<int> ExecuteAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default)
        {
            GuestCommands.Add(command.Command);
            return Task.FromResult(0);
        }
    }

    /// <summary>Engine whose <c>StartAsync</c> always fails, to prove fault handling.</summary>
    private sealed class ThrowingStartEngine : ISandboxEngine
    {
        public string Name => "throwing";

        public SandboxEngineKind Kind => SandboxEngineKind.HcsSandbox;

        public SandboxEngineCapabilities Capabilities => new() { SupportsMultipleInstances = true, SupportsCheckpoints = true };

        public bool IsAvailable => true;

        public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SandboxInstanceId>>(Array.Empty<SandboxInstanceId>());

        public Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");

        public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult<SandboxRuntimeInfo?>(null);
    }

    /// <summary>Single-instance engine that reports another running instance.</summary>
    private sealed class SingleInstanceEngine : ISandboxEngine
    {
        private readonly SandboxInstanceId _running;

        public SingleInstanceEngine(SandboxInstanceId running) => _running = running;

        public string Name => "single";

        public SandboxEngineKind Kind => SandboxEngineKind.HcsSandbox;

        public SandboxEngineCapabilities Capabilities => new() { SupportsMultipleInstances = false, SupportsCheckpoints = true };

        public bool IsAvailable => true;

        public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SandboxInstanceId>>(new[] { _running });

        public Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("StartAsync must not be reached for a single-instance engine.");

        public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult<SandboxRuntimeInfo?>(null);
    }
}
