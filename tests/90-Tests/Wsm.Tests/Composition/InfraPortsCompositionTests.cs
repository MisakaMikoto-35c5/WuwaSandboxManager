using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Wsm.Domain;
using Wsm.Domain.ValueObjects;
using Wsm.Connection.Legacy;
using Wsm.Connection.RdpBase;
using Wsm.Connection.Session;
using Wsm.Host.Elevation;
using Wsm.Host.Environment;
using Wsm.Host.Policy;
using Wsm.Persistence;
using Wsm.Recipe.Serialization;
using Wsm.SandboxServer.Client;
using Xunit;

namespace Wsm.Tests.Composition;

/// <summary>
/// Boots the composed Phase-3 infrastructure module graph and proves the public
/// ports are wired. In particular it guards the module-owned
/// <see cref="ISandboxRecipeSerializer"/> contract: the persistence repository
/// must receive the serializer registered by <c>WsbRecipeSerializationModule</c>
/// rather than the unregistered Domain-ported type.
/// </summary>
public sealed class InfraPortsCompositionTests
{
    [Fact]
    public async Task Infra_port_modules_resolve_all_nine_ports()
    {
        using var app = await AbpApplicationFactory.CreateAsync<InfraPortsTestModule>();
        await app.InitializeAsync();

        try
        {
            var services = app.ServiceProvider;

            Assert.NotNull(services.GetRequiredService<ISandboxInstanceRepository>());
            Assert.NotNull(services.GetRequiredService<ISandboxRecipeSerializer>());
            Assert.NotNull(services.GetRequiredService<ISandboxEnvironment>());
            Assert.NotNull(services.GetRequiredService<IMultiInstancePolicy>());
            Assert.NotNull(services.GetRequiredService<IElevationService>());
            Assert.NotNull(services.GetRequiredService<ISandboxServerClient>());
            Assert.NotNull(services.GetRequiredService<ISessionLauncher>());
            Assert.NotNull(services.GetRequiredService<ILegacySandboxLauncher>());
            Assert.NotNull(services.GetRequiredService<IRdpBaseConnectorFactory>());
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Repository_round_trips_an_instance_through_the_serializer()
    {
        using var app = await AbpApplicationFactory.CreateAsync<InfraPortsTestModule>();
        await app.InitializeAsync();

        try
        {
            var repository = app.ServiceProvider.GetRequiredService<ISandboxInstanceRepository>();
            var instance = SandboxInstance.Create(new SandboxName("T"));

            await repository.SaveAsync(instance);
            var loaded = await repository.GetAsync(instance.Id);

            Assert.NotNull(loaded);
            Assert.Equal(instance.Id, loaded!.Id);
            Assert.Equal(instance.Name, loaded.Name);
            Assert.Equal(instance.State, loaded.State);
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }
}
