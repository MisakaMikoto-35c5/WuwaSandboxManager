using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Wsm.Engines.Abstractions.Internal;
using Xunit;

namespace Wsm.Tests.Bootstrap;

/// <summary>
/// Covers the <see cref="SandboxEngineRegistry"/> fail-fast invariant (duplicate
/// <see cref="SandboxEngineKind"/>) and its kind lookup.
/// </summary>
public sealed class SandboxEngineRegistryTests
{
    [Fact]
    public void Constructor_throws_when_two_engines_share_a_kind()
    {
        var engines = new ISandboxEngine[]
        {
            new StubEngine(SandboxEngineKind.HcsSandbox),
            new StubEngine(SandboxEngineKind.HcsSandbox),
        };

        var exception = Assert.Throws<SandboxDomainException>(() => new SandboxEngineRegistry(engines));
        Assert.Contains(nameof(SandboxEngineKind.HcsSandbox), exception.Message);
    }

    [Fact]
    public void Find_returns_engine_of_requested_kind_for_distinct_kinds()
    {
        var hcs = new StubEngine(SandboxEngineKind.HcsSandbox);
        var windowsSandbox = new StubEngine(SandboxEngineKind.WindowsSandbox);

        var registry = new SandboxEngineRegistry(new ISandboxEngine[] { hcs, windowsSandbox });

        Assert.Same(hcs, registry.Find(SandboxEngineKind.HcsSandbox));
        Assert.Same(windowsSandbox, registry.Find(SandboxEngineKind.WindowsSandbox));
        Assert.Null(registry.Find(SandboxEngineKind.WindowsContainer));
    }

    /// <summary>Minimal engine stub; only <see cref="Kind"/> is meaningful.</summary>
    private sealed class StubEngine(SandboxEngineKind kind) : ISandboxEngine
    {
        public string Name => "stub";

        public SandboxEngineKind Kind { get; } = kind;

        public SandboxEngineCapabilities Capabilities => SandboxEngineCapabilities.None;

        public bool IsAvailable => false;

        public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task MapFolderAsync(SandboxInstanceId id, MappedFolder folder, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<int> ExecuteAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<SandboxCheckpoint> SaveCheckpointAsync(SandboxInstanceId id, string name, SandboxCheckpointType type, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<Guid> RestoreCheckpointAsync(SandboxInstanceId id, SandboxCheckpoint checkpoint, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }
}
