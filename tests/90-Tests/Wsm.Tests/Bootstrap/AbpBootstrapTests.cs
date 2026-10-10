using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using WindowsSandboxManager.Domain;
using Wsm.Engines.Abstractions;
using Xunit;

namespace Wsm.Tests.Bootstrap;

/// <summary>
/// Verifies the ABP module graph boots and that the core services are wired by
/// convention. Uses <see cref="AbpApplicationFactory.CreateAsync{TStartupModule}"/>
/// which returns an <c>IAbpApplicationWithInternalServiceProvider</c>.
/// </summary>
public sealed class AbpBootstrapTests
{
    [Fact]
    public async Task Bootstrap_resolves_registry_and_domain_module_without_duplicates()
    {
        using var app = await AbpApplicationFactory.CreateAsync<TestModule>();
        await app.InitializeAsync();

        try
        {
            // The registry is registered by convention ([ExposeServices] + ISingletonDependency).
            var registry = app.ServiceProvider.GetRequiredService<ISandboxEngineRegistry>();
            Assert.NotNull(registry);

            // No engines are implemented yet in this phase.
            Assert.Empty(registry.Engines);

            // ABP registers each module class as a singleton; resolving it proves the
            // module graph loaded and that there are no duplicate registrations.
            var domainModules = app.ServiceProvider.GetServices<SandboxDomainModule>().ToArray();
            Assert.Single(domainModules);
            Assert.NotNull(app.ServiceProvider.GetRequiredService<SandboxDomainModule>());
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }
}
