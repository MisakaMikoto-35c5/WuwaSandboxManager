using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.ContainerDesktop.Internal;

/// <summary>
/// Default <see cref="IContainerDesktopProviderRegistry"/>: picks the provider named
/// by the sandbox's manifest, falling back to the kind, then to the first supported
/// provider. Because selection is data-driven, adding a better mechanism later is a
/// new provider plus a recipe change - no caller changes.
/// </summary>
internal sealed class ContainerDesktopProviderRegistry : IContainerDesktopProviderRegistry, ISingletonDependency
{
    private readonly ILogger<ContainerDesktopProviderRegistry> _logger;

    public ContainerDesktopProviderRegistry(
        IEnumerable<IContainerDesktopProvider> providers,
        ILogger<ContainerDesktopProviderRegistry> logger)
    {
        _logger = logger;
        Providers = providers
            .OrderBy(p => p.Kind)
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (Providers.Count == 0)
        {
            _logger.LogWarning("No container desktop provider is registered.");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IContainerDesktopProvider> Providers { get; }

    /// <inheritdoc />
    public IContainerDesktopProvider? Find(string id)
        => Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <inheritdoc />
    public IContainerDesktopProvider Resolve(SandboxRecipe recipe)
    {
        var plan = recipe.Desktop;

        if (!string.IsNullOrWhiteSpace(plan.Provider))
        {
            var named = Find(plan.Provider!);
            if (named is null)
            {
                throw new SandboxValidationException(
                    $"The sandbox recipe asks for the container desktop provider '{plan.Provider}', which is " +
                    $"not registered. Available: {string.Join(", ", Providers.Select(p => p.Id))}.");
            }

            if (!named.IsSupported)
            {
                throw new SandboxBackendException(
                    $"The container desktop provider '{named.Id}' cannot run on this host.", 0);
            }

            return named;
        }

        if (plan.Kind == ContainerDesktopKind.None)
        {
            throw new SandboxValidationException(
                "The sandbox recipe does not request a container desktop (Desktop.Kind is None).");
        }

        var byKind = Providers.FirstOrDefault(p => p.Kind == plan.Kind && p.IsSupported)
            ?? throw new SandboxBackendException(
                $"No container desktop provider for '{plan.Kind}' is available on this host.", 0);

        _logger.LogDebug(
            "Container desktop provider for kind {Kind} resolved to '{Provider}'.", plan.Kind, byKind.Id);
        return byKind;
    }
}
