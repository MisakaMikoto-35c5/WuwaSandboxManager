using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using WindowsSandboxManager.Application.Abstractions;
using WindowsSandboxManager.Application.Backends;
using WindowsSandboxManager.Domain.Abstractions;

namespace WindowsSandboxManager.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the application services. Concrete backends, the repository,
    /// the connector, the serializer and the environment are expected to be
    /// registered by the infrastructure layer.
    /// </summary>
    public static IServiceCollection AddSandboxApplication(this IServiceCollection services)
    {
        services.TryAddSingleton<IDomainEventDispatcher, NullDomainEventDispatcher>();
        services.TryAddSingleton<ISandboxBackendSelector, DefaultSandboxBackendSelector>();
        services.TryAddSingleton<ISandboxApplicationService, SandboxApplicationService>();
        return services;
    }
}
