using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Application;
using Wsm.Application.EngineSelection;
using Wsm.Application.EngineSelection.Internal;
using Wsm.Engines.Abstractions;
using Wsm.Engines.Abstractions.Internal;
using Xunit;

namespace Wsm.Tests.Engines;

/// <summary>
/// Covers engine registration and the deterministic selection rules of
/// <see cref="EngineSelector"/> (preferred engine, availability, capability
/// match, explicit kind ordering). Selection logic is exercised with in-file
/// stub engines so it does not depend on host elevation.
/// </summary>
public sealed class EngineSelectionTests
{
    [Fact]
    public async Task All_three_engines_register_and_are_enumerable()
    {
        using var app = await AbpApplicationFactory.CreateAsync<EnginesTestModule>();
        await app.InitializeAsync();

        try
        {
            var engines = app.ServiceProvider.GetServices<ISandboxEngine>().ToArray();

            Assert.Equal(3, engines.Length);
            Assert.Contains(engines, engine => engine.Kind == SandboxEngineKind.HcsSandbox);
            Assert.Contains(engines, engine => engine.Kind == SandboxEngineKind.WindowsSandbox);

            // The reserved Windows Container engine registers but is never available.
            var container = Assert.Single(engines, engine => engine.Kind == SandboxEngineKind.WindowsContainer);
            Assert.False(container.IsAvailable);

            // The registry fast-fails on duplicate kinds, so resolving it proves
            // the engines are distinct.
            Assert.NotNull(app.ServiceProvider.GetRequiredService<ISandboxEngineRegistry>());
            Assert.NotNull(app.ServiceProvider.GetRequiredService<ISandboxEngineSelector>());
            Assert.NotNull(app.ServiceProvider.GetRequiredService<ISandboxManager>());
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Fact]
    public void Select_returns_preferred_engine_when_available()
    {
        var hcs = new StubEngine(SandboxEngineKind.HcsSandbox, available: true);
        var windowsSandbox = new StubEngine(SandboxEngineKind.WindowsSandbox, available: true);
        var selector = Selector(hcs, windowsSandbox);

        var selected = selector.Select(Request(preferred: SandboxEngineKind.WindowsSandbox));

        Assert.Same(windowsSandbox, selected);
    }

    [Fact]
    public void Select_defaults_to_hcs_when_both_available()
    {
        var hcs = new StubEngine(SandboxEngineKind.HcsSandbox, available: true);
        var windowsSandbox = new StubEngine(SandboxEngineKind.WindowsSandbox, available: true);
        var selector = Selector(hcs, windowsSandbox);

        var selected = selector.Select(Request());

        Assert.Equal(SandboxEngineKind.HcsSandbox, selected.Kind);
    }

    [Fact]
    public void Select_falls_back_to_windows_sandbox_when_hcs_unavailable()
    {
        var hcs = new StubEngine(SandboxEngineKind.HcsSandbox, available: false);
        var windowsSandbox = new StubEngine(SandboxEngineKind.WindowsSandbox, available: true);
        var selector = Selector(hcs, windowsSandbox);

        var selected = selector.Select(Request());

        Assert.Equal(SandboxEngineKind.WindowsSandbox, selected.Kind);
    }

    [Fact]
    public void Select_prefers_engine_supporting_cpu_configuration()
    {
        var hcs = new StubEngine(
            SandboxEngineKind.HcsSandbox,
            available: true,
            capabilities: new SandboxEngineCapabilities { SupportsCpuConfiguration = true });
        var windowsSandbox = new StubEngine(SandboxEngineKind.WindowsSandbox, available: true);
        var selector = Selector(hcs, windowsSandbox);

        var recipe = SandboxRecipe.Default with { Cpu = NonDefaultCpu() };
        var selected = selector.Select(Request(recipe: recipe));

        Assert.Equal(SandboxEngineKind.HcsSandbox, selected.Kind);
        Assert.True(selected.Capabilities.SupportsCpuConfiguration);
    }

    [Fact]
    public void Select_throws_when_no_engine_is_available()
    {
        var hcs = new StubEngine(SandboxEngineKind.HcsSandbox, available: false);
        var windowsSandbox = new StubEngine(SandboxEngineKind.WindowsSandbox, available: false);
        var selector = Selector(hcs, windowsSandbox);

        Assert.Throws<SandboxNotSupportedException>(() => selector.Select(Request()));
    }

    [Fact]
    public async Task Optional_members_throw_not_supported_by_default()
    {
        // Default interface members are only reachable through the interface.
        ISandboxEngine engine = new StubEngine(SandboxEngineKind.HcsSandbox, available: true);

        await Assert.ThrowsAsync<SandboxNotSupportedException>(
            () => engine.MapFolderAsync(SandboxInstanceId.New(), new MappedFolder(@"C:\data")));
        await Assert.ThrowsAsync<SandboxNotSupportedException>(
            () => engine.ListCheckpointsAsync(SandboxInstanceId.New()));
    }

    private static ISandboxEngineSelector Selector(params ISandboxEngine[] engines)
        => new EngineSelector(new SandboxEngineRegistry(engines));

    private static SandboxStartRequest Request(
        SandboxEngineKind? preferred = null,
        SandboxRecipe? recipe = null)
        => new()
        {
            Id = SandboxInstanceId.New(),
            Recipe = recipe ?? SandboxRecipe.Default,
            PreferredEngine = preferred,
        };

    private static SandboxCpuConfiguration NonDefaultCpu()
    {
        var current = SandboxCpuConfiguration.Default().CoreCount;
        return new SandboxCpuConfiguration(current == 1 ? 2 : 1);
    }

    /// <summary>Minimal engine stub; only Kind/Availability/Capabilities are meaningful.</summary>
    private sealed class StubEngine : ISandboxEngine
    {
        public StubEngine(SandboxEngineKind kind, bool available, SandboxEngineCapabilities? capabilities = null)
        {
            Kind = kind;
            IsAvailable = available;
            Capabilities = capabilities ?? SandboxEngineCapabilities.None;
        }

        public string Name => $"stub:{Kind}";

        public SandboxEngineKind Kind { get; }

        public SandboxEngineCapabilities Capabilities { get; }

        public bool IsAvailable { get; }

        public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<SandboxInstanceId>>(Array.Empty<SandboxInstanceId>());

        public Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.FromResult<SandboxRuntimeInfo?>(null);
    }
}
