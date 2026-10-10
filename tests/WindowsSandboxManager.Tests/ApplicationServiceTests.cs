using Microsoft.Extensions.Logging.Abstractions;
using WindowsSandboxManager.Application;
using WindowsSandboxManager.Application.Abstractions;
using WindowsSandboxManager.Application.Backends;
using WindowsSandboxManager.Application.Dtos;
using WindowsSandboxManager.Domain;
using WindowsSandboxManager.Domain.Abstractions;
using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;
using Xunit;

namespace WindowsSandboxManager.Tests;

public class ApplicationServiceTests
{
    [Fact]
    public async Task Start_is_refused_when_another_instance_is_running_on_a_single_instance_host()
    {
        var repo = new InMemoryRepository();
        var a = SandboxInstance.Create(new SandboxName("A"));
        var b = SandboxInstance.Create(new SandboxName("B"));
        await repo.SaveAsync(a);
        await repo.SaveAsync(b);

        var backend = new FakeBackend(supportsMultipleInstances: false)
        {
            Running = { a.Id },
        };

        var service = BuildService(repo, backend);

        var ex = await Assert.ThrowsAsync<MultipleInstancesNotAllowedException>(
            () => service.StartAsync(b.Id.Value, SandboxConnectionMode.FullDesktop));

        Assert.Contains(a.Id.Value.ToString(), ex.Message);
    }

    [Fact]
    public async Task Start_succeeds_when_no_other_instance_is_running()
    {
        var repo = new InMemoryRepository();
        var a = SandboxInstance.Create(new SandboxName("A"));
        await repo.SaveAsync(a);

        var backend = new FakeBackend(supportsMultipleInstances: false);
        var service = BuildService(repo, backend);

        var dto = await service.StartAsync(a.Id.Value, SandboxConnectionMode.RemoteApp);

        Assert.Equal(SandboxState.Running, dto.State);
        Assert.NotNull(dto.Runtime);
        Assert.Equal(RdpTransportKind.NamedPipe, dto.Runtime!.Transport);
    }

    [Fact]
    public async Task Start_is_allowed_when_the_backend_supports_multiple_instances()
    {
        var repo = new InMemoryRepository();
        var a = SandboxInstance.Create(new SandboxName("A"));
        var b = SandboxInstance.Create(new SandboxName("B"));
        await repo.SaveAsync(a);
        await repo.SaveAsync(b);

        var backend = new FakeBackend(supportsMultipleInstances: true) { Running = { a.Id } };
        var service = BuildService(repo, backend);

        var dto = await service.StartAsync(b.Id.Value, SandboxConnectionMode.FullDesktop);
        Assert.Equal(SandboxState.Running, dto.State);
    }

    private static SandboxApplicationService BuildService(ISandboxRepository repo, ISandboxBackend backend)
        => new(
            repo,
            new DefaultSandboxBackendSelector(new[] { backend }),
            new FakeConnector(),
            new FakeEnvironment(),
            new NullDomainEventDispatcher(),
            NullLogger<SandboxApplicationService>.Instance);

    private sealed class InMemoryRepository : ISandboxRepository
    {
        private readonly Dictionary<Guid, SandboxInstance> _items = new();

        public Task SaveAsync(SandboxInstance instance, CancellationToken cancellationToken = default)
        {
            _items[instance.Id.Value] = instance;
            return Task.CompletedTask;
        }

        public Task<SandboxInstance?> GetAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult(_items.TryGetValue(id.Value, out var instance) ? instance : null);

        public Task<IReadOnlyList<SandboxInstance>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SandboxInstance>>(_items.Values.ToArray());

        public Task DeleteAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
        {
            _items.Remove(id.Value);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBackend : ISandboxBackend
    {
        public FakeBackend(bool supportsMultipleInstances)
            => Capabilities = new SandboxBackendCapabilities
            {
                SupportsMultipleInstances = supportsMultipleInstances,
                SupportsRuntimeFolderMapping = true,
                SupportsProcessExecution = true,
                SupportsEnumeration = true,
            };

        public string Name => "Fake";

        public SandboxBackendCapabilities Capabilities { get; }

        public List<SandboxInstanceId> Running { get; } = new();

        public Task<IReadOnlyList<SandboxInstanceId>> EnumerateRunningAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SandboxInstanceId>>(Running.ToArray());

        public Task<SandboxRuntimeInfo> StartAsync(SandboxRecipe recipe, SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxRuntimeInfo
            {
                SandboxId = id,
                VmId = Guid.NewGuid(),
                Transport = RdpTransportKind.NamedPipe,
                RailMode = recipe.RailMode,
                UserName = "WDAGUtilityAccount",
            });

        public Task ShutdownAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
        {
            Running.Remove(id);
            return Task.CompletedTask;
        }

        public Task MapFolderAsync(SandboxInstanceId id, MappedFolder folder, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<int> ExecuteAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<SandboxRuntimeInfo?> GetRuntimeInfoAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult<SandboxRuntimeInfo?>(null);

        public Task<SandboxTerminationReason> WaitForTerminationAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult(SandboxTerminationReason.UserInitiatedShutdown);
    }

    private sealed class FakeConnector : ISandboxSessionConnector
    {
        public Task ConnectAsync(SandboxInstanceId id, SandboxRecipe recipe, SandboxRuntimeInfo runtime, SandboxConnectionMode mode, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeEnvironment : ISandboxEnvironment
    {
        public bool IsSupported => true;
        public bool IsElevated => false;
        public bool IsMultipleInstancesAllowed => false;
        public string? PackageInstallPath => null;
        public string? ServerPipeName => "wsandbox\\test";
        public string? LegacyExecutablePath => null;
        public string? RemoteSessionExecutablePath => null;
        public string? ServerExecutablePath => null;
        public bool EnsureServerRunning() => true;
    }
}
