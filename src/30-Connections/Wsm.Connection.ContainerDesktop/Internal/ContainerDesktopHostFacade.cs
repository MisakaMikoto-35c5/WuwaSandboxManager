using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;

namespace Wsm.Connection.ContainerDesktop.Internal;

/// <summary>
/// Stable entry point over the registered <see cref="IContainerDesktopProvider"/>s.
/// <para>
/// The provider is chosen from the <b>sandbox's manifest</b>
/// (<see cref="SandboxRecipe.Desktop"/>) - that is the seam that keeps the mechanism
/// replaceable: callers say "give me the desktop of this sandbox", recipes say which
/// transport produces it, and providers are plug-ins.
/// </para>
/// </summary>
internal sealed class ContainerDesktopHost : IContainerDesktopHost, ISingletonDependency
{
    private readonly IContainerDesktopProviderRegistry _registry;
    private readonly ISandboxEngineRegistry _engines;
    private readonly ILogger<ContainerDesktopHost> _logger;

    public ContainerDesktopHost(
        IContainerDesktopProviderRegistry registry,
        ISandboxEngineRegistry engines,
        ILogger<ContainerDesktopHost> logger)
    {
        _registry = registry;
        _engines = engines;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsSupported => _registry.Providers.Any(p => p.IsSupported);

    /// <inheritdoc />
    public IReadOnlyList<string> ProviderIds => _registry.Providers.Select(p => p.Id).ToArray();

    /// <inheritdoc />
    public IReadOnlyList<ContainerDesktopSession> Sessions
        => _registry.Providers.SelectMany(p => p.Sessions).ToArray();

    /// <inheritdoc />
    public IContainerDesktopProvider ResolveProvider(SandboxRecipe recipe) => _registry.Resolve(recipe);

    /// <inheritdoc />
    public ContainerDesktopSession? Get(SandboxInstanceId id)
        => _registry.Providers.Select(p => p.Get(id)).FirstOrDefault(session => session is not null);

    /// <inheritdoc />
    public Task<ContainerDesktopSession> StartAsync(
        SandboxInstanceId id, CancellationToken cancellationToken = default)
        => StartAsync(id, SandboxRecipe.Default, cancellationToken);

    /// <inheritdoc />
    public async Task<ContainerDesktopSession> StartAsync(
        SandboxInstanceId id, SandboxRecipe recipe, CancellationToken cancellationToken = default)
    {
        var provider = _registry.Resolve(recipe);
        var root = ResolveContainerRoot(id);
        _logger.LogInformation(
            "Starting the container desktop for {SandboxId} with provider '{Provider}'.", id, provider.Id);

        var session = await provider
            .StartAsync(new ContainerDesktopRequest(id, id.ToString(), root, recipe), cancellationToken)
            .ConfigureAwait(false);

        return recipe.Desktop.PresentToInteractiveSession
            ? await provider.PresentAsync(id, cancellationToken).ConfigureAwait(false)
            : session;
    }

    /// <inheritdoc />
    public Task<ContainerDesktopSession> AttachAsync(
        string computeSystemId, string containerRoot, CancellationToken cancellationToken = default)
        => AttachAsync(computeSystemId, containerRoot, SandboxRecipe.Default, cancellationToken);

    /// <inheritdoc />
    public async Task<ContainerDesktopSession> AttachAsync(
        string computeSystemId, string containerRoot, SandboxRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(computeSystemId, out var value))
        {
            throw new SandboxBackendException($"'{computeSystemId}' is not a compute-system/sandbox id.", 0);
        }

        var provider = _registry.Resolve(recipe);
        var request = new ContainerDesktopRequest(
            SandboxInstanceId.From(value), computeSystemId, containerRoot, recipe);

        var session = await provider.StartAsync(request, cancellationToken).ConfigureAwait(false);
        return recipe.Desktop.PresentToInteractiveSession
            ? await provider.PresentAsync(session.SandboxId, cancellationToken).ConfigureAwait(false)
            : session;
    }

    /// <inheritdoc />
    public Task<ContainerDesktopSession> PresentAsync(
        SandboxInstanceId id, CancellationToken cancellationToken = default)
        => Require(id).PresentAsync(id, cancellationToken);

    /// <inheritdoc />
    public Task SendInputAsync(SandboxInstanceId id, string command, CancellationToken cancellationToken = default)
        => Require(id).SendInputAsync(id, command, cancellationToken);

    /// <inheritdoc />
    public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
        => Require(id).StopAsync(id, cancellationToken);

    /// <inheritdoc />
    public async Task StopAsync(
        string computeSystemId, string containerRoot, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(computeSystemId, out var value))
        {
            throw new SandboxBackendException($"'{computeSystemId}' is not a compute-system/sandbox id.", 0);
        }

        var id = SandboxInstanceId.From(value);
        foreach (var provider in _registry.Providers)
        {
            if (provider.Get(id) is not null)
            {
                await provider.StopAsync(id, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (provider is IContainerDesktopRootStop rootStop)
            {
                await rootStop.StopAsync(computeSystemId, containerRoot, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Finds the provider that owns a live session, or explains why it cannot.</summary>
    private IContainerDesktopProvider Require(SandboxInstanceId id)
        => _registry.Providers.FirstOrDefault(p => p.Get(id) is not null)
           ?? throw new SandboxBackendException(
               $"No container desktop is running for {id}; check the sandbox's Desktop plan and start it first.", 0);

    /// <summary>The host-visible root of the running container, through the engine capability.</summary>
    private string ResolveContainerRoot(SandboxInstanceId id)
    {
        var engine = _engines.Find(SandboxEngineKind.WindowsContainer)
            ?? throw new SandboxBackendException("The Windows container engine is not registered.", 0);

        if (engine is not IContainerFileSystemAccess fileSystem)
        {
            throw new SandboxBackendException(
                "The active container engine cannot expose the container filesystem, so a host-side desktop " +
                "channel cannot be set up.", 0);
        }

        return fileSystem.GetContainerRoot(id)
            ?? throw new SandboxBackendException($"Container {id} is not running.", 0);
    }
}
