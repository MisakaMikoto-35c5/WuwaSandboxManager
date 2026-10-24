using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Application;
using Wsm.Domain;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Wsm.Service.Client;
using Wsm.Service.Contracts.Internal;
using Xunit;

namespace Wsm.Tests.Service;

/// <summary>
/// Tests for <see cref="ServiceBackedSandboxManager"/>: VM-lifecycle calls route
/// to the agent while it is available, fail over to the in-process manager when
/// it dies, and definition-level operations never leave the process.
/// </summary>
public sealed class ServiceBackedSandboxManagerTests
{
    private static readonly Guid Id = Guid.NewGuid();

    [Fact]
    public async Task Start_routes_to_the_agent_and_fills_the_runtime_from_its_reply()
    {
        var inner = new FakeInnerManager();
        var service = new FakeServiceClient();
        var runtime = new SandboxRuntimeInfo { SandboxId = SandboxInstanceId.From(Id), VmId = Guid.NewGuid() };
        service.StartResult = runtime;
        var manager = Create(inner, service);

        var dto = await manager.StartAsync(Id, SandboxConnectionMode.FullDesktop);

        Assert.Equal(1, service.StartCalls);
        Assert.Equal(0, inner.StartCalls);
        Assert.Equal(1, inner.GetCalls); // DTO read-back
        Assert.Same(runtime, dto.Runtime);
    }

    [Fact]
    public async Task Start_falls_back_in_process_when_the_agent_dies_and_stays_there()
    {
        var inner = new FakeInnerManager();
        var service = new FakeServiceClient
        {
            StartException = new SandboxBackendException("pipe broken", WsmServiceStatus.ServerUnavailable),
        };
        var manager = Create(inner, service);

        var dto = await manager.StartAsync(Id, SandboxConnectionMode.FullDesktop);

        Assert.Equal(1, service.StartCalls);
        Assert.Equal(1, inner.StartCalls);
        Assert.Same(inner.StartResult, dto);

        // The failover is remembered: later lifecycle calls go straight to the
        // in-process manager without touching the dead agent.
        await manager.StopAsync(Id);
        Assert.Equal(0, service.StopCalls);
        Assert.Equal(1, inner.StopCalls);
    }

    [Fact]
    public async Task Lifecycle_calls_use_the_in_process_manager_when_no_agent_answers()
    {
        var inner = new FakeInnerManager();
        var service = new FakeServiceClient { EnsureAvailableResult = false };
        var manager = Create(inner, service);

        await manager.StartAsync(Id, SandboxConnectionMode.FullDesktop);
        await manager.StopAsync(Id);

        Assert.Equal(1, inner.StartCalls);
        Assert.Equal(1, inner.StopCalls);
        Assert.Equal(0, service.StartCalls);
        Assert.Equal(0, service.StopCalls);
    }

    [Fact]
    public async Task Definition_operations_always_run_in_process()
    {
        var inner = new FakeInnerManager();
        var service = new FakeServiceClient();
        var manager = Create(inner, service);

        await manager.CreateAsync("test", SandboxRecipe.Default);
        await manager.ListAsync();
        await manager.GetAsync(Id);

        Assert.Equal(1, inner.CreateCalls);
        Assert.Equal(1, inner.ListCalls);
        Assert.Equal(1, inner.GetCalls);
        Assert.Equal(0, service.EnsureAvailableCalls);
        Assert.Equal(0, service.StartCalls);
    }

    [Fact]
    public async Task Stop_routes_to_the_agent_when_available()
    {
        var inner = new FakeInnerManager();
        var service = new FakeServiceClient();
        var manager = Create(inner, service);

        await manager.StopAsync(Id);

        Assert.Equal(1, service.StopCalls);
        Assert.Equal(0, inner.StopCalls);
    }

    private static ServiceBackedSandboxManager Create(FakeInnerManager inner, FakeServiceClient service)
        => new(inner, service, @"C:\data", NullLogger<ServiceBackedSandboxManager>.Instance);

    private sealed class FakeInnerManager : ISandboxManager
    {
        public int CreateCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int GetCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public SandboxInstanceDto StartResult { get; } = Dto();

        public Task<SandboxInstanceDto> CreateAsync(string? name, SandboxRecipe recipe, SandboxEngineKind? engine = null, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult(Dto());
        }

        public Task<IReadOnlyList<SandboxInstanceDto>> ListAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<SandboxInstanceDto>>([Dto()]);
        }

        public Task<SandboxInstanceDto> ImportAsync(string manifestPath, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<SandboxInstanceDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(Dto());
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<SandboxInstanceDto> StartAsync(Guid id, SandboxConnectionMode mode, CancellationToken cancellationToken = default)
        {
            StartCalls++;
            return Task.FromResult(StartResult);
        }

        public Task StopAsync(Guid id, CancellationToken cancellationToken = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Guid>> ListRunningAsync(SandboxEngineKind? preferredEngine = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<SandboxCheckpoint> SaveCheckpointAsync(Guid id, string name, SandboxCheckpointType type, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<SandboxCheckpoint>> ListCheckpointsAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<Guid> RestoreCheckpointAsync(Guid id, Guid checkpointId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<SandboxRuntimeInfo?> GetRuntimeAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<SandboxRuntimeInfo> PrepareConnectAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task ConnectAsync(Guid id, SandboxConnectionMode mode, string? remoteAppProgram = null, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<string>> GetIpAddressesAsync(Guid id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task MapFolderAsync(Guid id, MappedFolder folder, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<int> ExecuteAsync(Guid id, string command, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<SandboxCapabilitiesDto> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        private static SandboxInstanceDto Dto()
            => new(Id, "test", SandboxState.Created, SandboxRecipe.Default, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private sealed class FakeServiceClient : IWsmServiceClient
    {
        public int EnsureAvailableCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public bool EnsureAvailableResult { get; set; } = true;
        public SandboxRuntimeInfo StartResult { get; set; } = new() { SandboxId = SandboxInstanceId.From(Id) };
        public Exception? StartException { get; set; }

        public Task<bool> EnsureAvailableAsync(string? dataFolder = null, CancellationToken ct = default)
        {
            EnsureAvailableCalls++;
            return Task.FromResult(EnsureAvailableResult);
        }

        public Task<SandboxRuntimeInfo> StartAsync(Guid id, SandboxConnectionMode mode, CancellationToken ct = default)
        {
            StartCalls++;
            return StartException is not null ? Task.FromException<SandboxRuntimeInfo>(StartException) : Task.FromResult(StartResult);
        }

        public Task StopAsync(Guid id, CancellationToken ct = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Guid>> EnumerateAsync(CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SandboxRuntimeInfo?> GetRuntimeAsync(Guid id, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<SandboxRuntimeInfo> PrepareConnectAsync(Guid id, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<GuestCommandResult> ExecuteAsync(Guid id, string commandLine, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task MapFolderAsync(Guid id, MappedFolder folder, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<string>> GetIpAddressesAsync(Guid id, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<Wsm.Service.Contracts.ContainerDesktopStatus> StartDesktopAsync(
            Guid id, string? provider = null, ContainerDesktopKind? kind = null, bool present = false, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task StopDesktopAsync(Guid id, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<Wsm.Service.Contracts.ContainerDesktopStatus> GetDesktopStatusAsync(Guid id, CancellationToken ct = default)
            => throw new NotImplementedException();

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(EnsureAvailableResult);

        public Task ShutdownAsync(CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
