using WindowsSandboxManager.Application.Backends;
using WindowsSandboxManager.Domain.Abstractions;
using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;
using Xunit;

namespace WindowsSandboxManager.Tests;

public class BackendSelectorTests
{
    [Fact]
    public void Unavailable_backend_is_skipped()
    {
        var hcs = new StubBackend("HCS", available: false, new SandboxBackendCapabilities
        {
            RequiresElevation = true,
            SupportsCpuConfiguration = true,
        });
        var grpc = new StubBackend("gRPC", available: true, new SandboxBackendCapabilities());

        var selector = new DefaultSandboxBackendSelector(new ISandboxBackend[] { hcs, grpc });
        var selected = selector.Select(SandboxRecipe.Default);

        Assert.Equal("gRPC", selected.Name);
    }

    [Fact]
    public void Available_hcs_is_preferred_and_honours_cpu()
    {
        var hcs = new StubBackend("HCS", available: true, new SandboxBackendCapabilities
        {
            SupportsCpuConfiguration = true,
            SupportsCustomStorageLocation = true,
        });
        var grpc = new StubBackend("gRPC", available: true, new SandboxBackendCapabilities());

        var selector = new DefaultSandboxBackendSelector(new ISandboxBackend[] { hcs, grpc });

        var withCpu = selector.Select(SandboxRecipe.Default with { Cpu = new SandboxCpuConfiguration(16) });
        Assert.Equal("HCS", withCpu.Name);

        var withStorage = selector.Select(SandboxRecipe.Default with
        {
            Storage = SandboxStorageOptions.Default.WithPath(@"E:\x"),
        });
        Assert.Equal("HCS", withStorage.Name);

        var plain = selector.Select(SandboxRecipe.Default);
        Assert.Equal("HCS", plain.Name);
    }

    [Fact]
    public void No_available_backend_throws()
    {
        var only = new StubBackend("HCS", available: false, new SandboxBackendCapabilities());
        var selector = new DefaultSandboxBackendSelector(new ISandboxBackend[] { only });

        Assert.Throws<SandboxNotSupportedException>(() => selector.Select(SandboxRecipe.Default));
    }

    private sealed class StubBackend : ISandboxBackend
    {
        public StubBackend(string name, bool available, SandboxBackendCapabilities capabilities)
        {
            Name = name;
            IsAvailable = available;
            Capabilities = capabilities;
        }

        public string Name { get; }

        public bool IsAvailable { get; }

        public SandboxBackendCapabilities Capabilities { get; }

        public Task<IReadOnlyList<SandboxInstanceId>> EnumerateRunningAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SandboxInstanceId>>(Array.Empty<SandboxInstanceId>());

        public Task<SandboxRuntimeInfo> StartAsync(SandboxRecipe recipe, SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult(new SandboxRuntimeInfo { SandboxId = id, VmId = id.Value });

        public Task ShutdownAsync(SandboxInstanceId id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MapFolderAsync(SandboxInstanceId id, MappedFolder folder, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> ExecuteAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        public Task<SandboxRuntimeInfo?> GetRuntimeInfoAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult<SandboxRuntimeInfo?>(null);

        public Task<SandboxTerminationReason> WaitForTerminationAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult(SandboxTerminationReason.UserInitiatedShutdown);
    }
}
