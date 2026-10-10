using WindowsSandboxManager.Domain;
using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Application;
using Wsm.Application.EngineSelection.Internal;
using Wsm.Application.Internal;
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

    private static ISandboxManager Manager(ISandboxInstanceRepository repository, ISandboxEngine engine)
    {
        var selector = new EngineSelector(new SandboxEngineRegistry(new ISandboxEngine[] { engine }));
        return new SandboxManager(repository, selector);
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
}
