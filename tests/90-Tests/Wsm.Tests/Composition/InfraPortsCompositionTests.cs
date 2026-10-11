using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
/// ports are wired. It also covers the portable <c>.wsm</c> persistence layout
/// (one self-contained file per sandbox with relative data/mapped paths).
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
    public async Task Repository_round_trips_an_instance()
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
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Repository_writes_and_deletes_a_single_wsm_file()
    {
        using var app = await AbpApplicationFactory.CreateAsync<InfraPortsTestModule>();
        await app.InitializeAsync();

        try
        {
            var repository = app.ServiceProvider.GetRequiredService<ISandboxInstanceRepository>();
            var instance = SandboxInstance.Create(new SandboxName("M"));

            await repository.SaveAsync(instance);
            var path = repository.GetManifestPath(instance.Id);

            Assert.True(File.Exists(path));
            Assert.EndsWith(".wsm", path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(instance.Id.Value, SandboxManifest.TryReadFile(path)?.Id);

            // No separate instance.json / recipe.wsb sidecars.
            var folder = Path.GetDirectoryName(path)!;
            Assert.False(File.Exists(Path.Combine(folder, "instance.json")));
            Assert.False(File.Exists(Path.Combine(folder, "recipe.wsb")));

            await repository.DeleteAsync(instance.Id);
            Assert.False(File.Exists(path));
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Wsm_file_is_portable_with_relative_paths()
    {
        using var app = await AbpApplicationFactory.CreateAsync<InfraPortsTestModule>();
        await app.InitializeAsync();

        try
        {
            var services = app.ServiceProvider;
            var configDataFolder = services
                .GetRequiredService<IOptions<SandboxPersistenceOptions>>().Value
                .ResolveConfigDataFolder();

            var repository = services.GetRequiredService<ISandboxInstanceRepository>();
            var recipe = SandboxRecipe.Default.WithMappedFolder(new MappedFolder(@"C:\external\share", readOnly: true));
            var instance = SandboxInstance.Create(new SandboxName("Portable"), recipe);

            await repository.SaveAsync(instance);
            var path = repository.GetManifestPath(instance.Id);

            // The .wsm sits in <configDataFolder>\<Name>\<Name>.wsm.
            Assert.Equal(
                Path.Combine(configDataFolder, "Portable"),
                Path.GetDirectoryName(path));
            Assert.Equal("Portable.wsm", Path.GetFileName(path));

            // On disk: relative data path, no absolute storage path, and an
            // outside-the-folder mapped folder stays absolute.
            var manifest = SandboxManifest.TryReadFile(path);
            Assert.NotNull(manifest);
            Assert.False(Path.IsPathFullyQualified(manifest!.DataPath!));
            Assert.Null(manifest.Recipe.Storage.StoragePath);
            Assert.Equal(@"C:\external\share", Assert.Single(manifest.Recipe.MappedFolders).HostFolder);

            // Move the whole folder elsewhere, then open the .wsm from there: the
            // relative data path resolves against the new location.
            var movedFolder = Path.Combine(Path.GetTempPath(), "wsm-moved-" + Guid.NewGuid());
            Directory.CreateDirectory(movedFolder);
            try
            {
                var movedPath = Path.Combine(movedFolder, Path.GetFileName(path));
                File.Copy(path, movedPath);

                var imported = await repository.ImportAsync(movedPath);

                Assert.Equal(instance.Id, imported.Id);
                Assert.Equal(movedFolder, imported.Recipe.Storage.StoragePath);
            }
            finally
            {
                Directory.Delete(movedFolder, recursive: true);
            }
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }
}
