using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Volo.Abp;
using Volo.Abp.Autofac;
using Wsm.Application;
using Wsm.Engines.Abstractions;
using Wsm.Localization;
using Xunit;

namespace Wsm.Tests.Bootstrap;

/// <summary>
/// Proves the production bootstrap path (plan §6.1) works with the optional
/// Autofac container: the same <see cref="TestModule"/> graph boots via
/// <c>UseAutofac()</c> and the core services resolve.
/// </summary>
public sealed class AutofacBootstrapTests
{
    [Fact]
    public async Task Autofac_bootstrap_resolves_registry_manager_and_localizer()
    {
        using var app = await AbpApplicationFactory.CreateAsync<TestModule>(
            options => options.UseAutofac());
        await app.InitializeAsync();

        try
        {
            var services = app.ServiceProvider;

            Assert.NotNull(services.GetRequiredService<ISandboxEngineRegistry>());
            Assert.NotNull(services.GetRequiredService<ISandboxManager>());
            Assert.NotNull(services.GetRequiredService<IStringLocalizer<WindowsSandboxManagerResource>>());
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }
}
