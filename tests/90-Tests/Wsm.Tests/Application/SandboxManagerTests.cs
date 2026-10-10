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

    private static ISandboxManager Manager(ISandboxInstanceRepository repository, ISandboxEngine engine)
    {
        var selector = new EngineSelector(new SandboxEngineRegistry(new ISandboxEngine[] { engine }));
        return new SandboxManager(repository, selector, new StubSessionLauncher(), new StubManifestStore());
    }

    /// <summary>Records manifest writes/deletes without touching the file system.</summary>
    private sealed class StubManifestStore : ISandboxManifestStore
    {
        public List<SandboxInstanceId> Saved { get; } = new();

        public List<SandboxInstanceId> Deleted { get; } = new();

        public string GetManifestPath(SandboxInstanceId id) => $"manifests/{id.Value:D}.wsbx";

        public Task SaveAsync(SandboxInstance instance, CancellationToken cancellationToken = default)
        {
            Saved.Add(instance.Id);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
        {
            Deleted.Add(id);
            return Task.CompletedTask;
        }
    }

    private sealed class StubSessionLauncher : ISessionLauncher
    {
        public SandboxInstanceId? LastId { get; private set; }

        public SandboxConnectionMode? LastMode { get; private set; }

        public Task LaunchAsync(
            SandboxInstanceId id,
            SandboxRuntimeInfo runtime,
            SandboxConnectionMode mode,
            CancellationToken cancellationToken = default)
        {
            LastId = id;
            LastMode = mode;
            return Task.CompletedTask;
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
    }

    private sealed class StubEngine : ISandboxEngine
    {
        private readonly List<SandboxCheckpoint> _checkpoints = new();

        public int SaveCount { get; private set; }

        public int ListCount { get; private set; }

        public Guid RestoredId { get; } = Guid.NewGuid();

        public SandboxCheckpoint? LastRestored { get; private set; }

        public string Name => "stub";

        public SandboxEngineKind Kind => SandboxEngineKind.HcsSandbox;

        public SandboxEngineCapabilities Capabilities => new() { SupportsCheckpoints = true };

        public bool IsAvailable => true;

        public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SandboxInstanceId>>(Array.Empty<SandboxInstanceId>());

        public Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxRuntimeInfo
            {
                SandboxId = request.Id,
                VmId = request.Id.Value,
                Transport = RdpTransportKind.NamedPipe,
            });

        public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult<SandboxRuntimeInfo?>(null);

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
