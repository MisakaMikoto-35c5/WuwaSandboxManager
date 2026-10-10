using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Volo.Abp;
using Volo.Abp.Modularity;
using Wsm.Hcs.Checkpoint;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Sandbox;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Storage;
using Xunit;

namespace Wsm.Tests.Hcs;

/// <summary>
/// Root startup module composing the five HCS capability modules, mirroring the
/// composition the HCS engine will use.
/// </summary>
[DependsOn(
    typeof(HcsInteropModule),
    typeof(HcsLayoutModule),
    typeof(SandboxVmJsonModule),
    typeof(HcsStorageModule),
    typeof(HcsCheckpointModule))]
public sealed class HcsTestModule : AbpModule
{
}

/// <summary>
/// Verifies the five HCS capability assemblies compose into an ABP module graph
/// and that each public interface is resolvable by convention.
/// </summary>
public sealed class HcsModuleGraphTests
{
    [Fact]
    public async Task Hcs_modules_bootstrap_and_resolve_all_five_interfaces()
    {
        using var app = await AbpApplicationFactory.CreateAsync<HcsTestModule>();
        await app.InitializeAsync();

        try
        {
            Assert.NotNull(app.ServiceProvider.GetRequiredService<IHcsClient>());
            Assert.NotNull(app.ServiceProvider.GetRequiredService<IHcsHostLayout>());
            Assert.NotNull(app.ServiceProvider.GetRequiredService<ISandboxVmJsonBuilder>());
            Assert.NotNull(app.ServiceProvider.GetRequiredService<ISandboxStorageProvisioner>());
            Assert.NotNull(app.ServiceProvider.GetRequiredService<ICheckpointService>());
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Fact]
    public async Task Sandbox_vm_json_builder_emits_expected_document()
    {
        using var app = await AbpApplicationFactory.CreateAsync<HcsTestModule>();
        await app.InitializeAsync();

        try
        {
            var builder = app.ServiceProvider.GetRequiredService<ISandboxVmJsonBuilder>();
            var definition = new HcsSandboxDefinition
            {
                Id = "11111111-2222-3333-4444-555555555555",
                BaseLayerPath = @"D:\images\base\Files",
                StorageDiskPath = @"E:\userdata\sandbox.vhdx",
                GuestStatePath = @"E:\userdata\sandbox.vmgs",
            };

            var json = builder.Build(definition);

            Assert.Contains("VirtualSmb", json);
            Assert.Contains("RunInSilo", json);
            Assert.Contains("Scsi", json);

            // The supplied storage path must round-trip through the SCSI attachment
            // (JSON escapes backslashes, so assert on the parsed document).
            using var doc = JsonDocument.Parse(json);
            var storagePath = doc.RootElement
                .GetProperty("VirtualMachine")
                .GetProperty("Devices")
                .GetProperty("Scsi")
                .GetProperty("primary")
                .GetProperty("Attachments")
                .GetProperty("0")
                .GetProperty("Path")
                .GetString();
            Assert.Equal(@"E:\userdata\sandbox.vhdx", storagePath);
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }
}
