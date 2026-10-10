using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Wsm.Hcs.Json.Sandbox;
using Xunit;

namespace Wsm.Tests.Hcs;

/// <summary>
/// JSON parity tests for the HCS Sandbox (VM) configuration builder, ported from
/// the legacy <c>HcsSandboxJsonTests</c> to drive <see cref="ISandboxVmJsonBuilder"/>
/// resolved from the ABP module graph (same pattern as
/// <see cref="HcsModuleGraphTests"/>).
/// </summary>
public sealed class SandboxVmJsonBuilderTests
{
    private static HcsSandboxDefinition Definition() => new()
    {
        Id = "11111111-2222-3333-4444-555555555555",
        BaseLayerPath = @"D:\images\base\Files",
        StorageDiskPath = @"E:\userdata\sandbox.vhdx",
        GuestStatePath = @"E:\userdata\sandbox.vmgs",
        MemoryMb = 8192,
        CpuCount = 12,
        EnableVirtualGpu = true,
        MappedFolders = new[] { new HcsMappedFolder(@"C:\data", @"C:\share", ReadOnly: true) },
    };

    private static async Task<T> WithBuilderAsync<T>(Func<ISandboxVmJsonBuilder, T> action)
    {
        using var app = await AbpApplicationFactory.CreateAsync<HcsTestModule>();
        await app.InitializeAsync();

        try
        {
            return action(app.ServiceProvider.GetRequiredService<ISandboxVmJsonBuilder>());
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Fact]
    public Task Custom_base_image_and_storage_paths_are_emitted() => WithBuilderAsync(builder =>
    {
        var json = builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);
        var vm = doc.RootElement.GetProperty("VirtualMachine");

        // Base OS image layer location (custom).
        var osShare = vm.GetProperty("Devices").GetProperty("VirtualSmb").GetProperty("Shares")[0];
        Assert.Equal("os", osShare.GetProperty("Name").GetString());
        Assert.Equal(@"D:\images\base\Files", osShare.GetProperty("Path").GetString());
        Assert.Equal(@"D:\images\base\Files", vm.GetProperty("RunInSilo").GetProperty("SiloBaseOsPath").GetString());

        // User-data disk location (custom).
        var attachment = vm.GetProperty("Devices").GetProperty("Scsi").GetProperty("primary")
            .GetProperty("Attachments").GetProperty("0");
        Assert.Equal(@"E:\userdata\sandbox.vhdx", attachment.GetProperty("Path").GetString());

        // Guest state location (custom).
        Assert.Equal(@"E:\userdata\sandbox.vmgs", vm.GetProperty("GuestState").GetProperty("GuestStateFilePath").GetString());
        return true;
    });

    [Fact]
    public Task Cpu_and_memory_are_emitted() => WithBuilderAsync(builder =>
    {
        var json = builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);
        var topology = doc.RootElement.GetProperty("VirtualMachine").GetProperty("ComputeTopology");

        Assert.Equal(12, topology.GetProperty("Processor").GetProperty("Count").GetInt32());
        Assert.Equal(8192u, topology.GetProperty("Memory").GetProperty("SizeInMB").GetUInt32());
        return true;
    });

    [Fact]
    public Task Mapped_folders_become_vsmb_shares() => WithBuilderAsync(builder =>
    {
        var json = builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);
        var shares = doc.RootElement.GetProperty("VirtualMachine").GetProperty("Devices")
            .GetProperty("VirtualSmb").GetProperty("Shares");

        Assert.Equal(2, shares.GetArrayLength());
        Assert.Equal(@"C:\data", shares[1].GetProperty("Path").GetString());
        Assert.True(shares[1].GetProperty("Options").GetProperty("ReadOnly").GetBoolean());
        return true;
    });

    [Fact]
    public Task Virtual_gpu_can_be_disabled() => WithBuilderAsync(builder =>
    {
        var json = builder.Build(Definition() with { EnableVirtualGpu = false });
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("VirtualMachine").GetProperty("Devices")
            .TryGetProperty("EnhancedModeVideo", out _));
        return true;
    });

    [Fact]
    public Task Restore_state_is_emitted_for_checkpoints() => WithBuilderAsync(builder =>
    {
        var json = builder.Build(Definition() with
        {
            TemplateSystemId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        });
        using var doc = JsonDocument.Parse(json);
        var restore = doc.RootElement.GetProperty("VirtualMachine").GetProperty("RestoreState");
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", restore.GetProperty("TemplateSystemId").GetString());
        return true;
    });

    [Fact]
    public Task Hv_socket_exposes_lsm_and_nlm_services() => WithBuilderAsync(builder =>
    {
        var json = builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);
        var table = doc.RootElement.GetProperty("VirtualMachine").GetProperty("Devices")
            .GetProperty("HvSocket").GetProperty("HvSocketConfig").GetProperty("ServiceTable");

        // HCS expects the service table as an object keyed by the service GUID
        // (an array is rejected at Construct time).
        Assert.Equal(JsonValueKind.Object, table.ValueKind);
        Assert.True(table.TryGetProperty(HcsSandboxConstants.LsmServiceId.ToString(), out _));
        Assert.True(table.TryGetProperty(HcsSandboxConstants.NlmServiceId.ToString(), out _));
        return true;
    });

    [Fact]
    public Task Licensing_device_is_emitted() => WithBuilderAsync(builder =>
    {
        var json = builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);
        var licensing = doc.RootElement.GetProperty("VirtualMachine").GetProperty("Devices")
            .GetProperty("Licensing");

        Assert.Equal(Guid.Empty.ToString(), licensing.GetProperty("ContainerID").GetString());
        Assert.Equal(0, licensing.GetProperty("PackageFamilyNames").GetArrayLength());
        return true;
    });

    [Fact]
    public Task Guest_crash_reporting_is_omitted_without_a_dump_path() => WithBuilderAsync(builder =>
    {
        var json = builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("VirtualMachine").GetProperty("Devices")
            .TryGetProperty("GuestCrashReporting", out _));
        return true;
    });

    [Fact]
    public Task Guest_crash_reporting_uses_the_supplied_dump_path() => WithBuilderAsync(builder =>
    {
        var json = builder.Build(Definition() with { DumpFilePath = @"E:\userdata\sandbox.dmp" });
        using var doc = JsonDocument.Parse(json);
        var settings = doc.RootElement.GetProperty("VirtualMachine").GetProperty("Devices")
            .GetProperty("GuestCrashReporting").GetProperty("WindowsCrashSettings");

        // HCS opens this file at Start; without a valid path it fails with
        // 0x80070003 / 0x80070002.
        Assert.Equal(@"E:\userdata\sandbox.dmp", settings.GetProperty("DumpFileName").GetString());
        Assert.Equal("Full", settings.GetProperty("DumpType").GetString());
        return true;
    });

    [Fact]
    public Task Enhanced_mode_video_omits_empty_access_sids() => WithBuilderAsync(builder =>
    {
        var json = builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);
        var options = doc.RootElement.GetProperty("VirtualMachine").GetProperty("Devices")
            .GetProperty("EnhancedModeVideo").GetProperty("ConnectionOptions");

        // An empty AccessSids array is meaningless (and rejected by some HCS
        // builds), so it must not be emitted; the named pipe always is.
        Assert.False(options.TryGetProperty("AccessSids", out _));
        Assert.Equal(@"\\.\pipe\11111111-2222-3333-4444-555555555555", options.GetProperty("NamedPipe").GetString());
        return true;
    });

    [Fact]
    public Task Network_adapter_is_omitted_without_an_endpoint() => WithBuilderAsync(builder =>
    {
        var json = builder.Build(Definition() with { EnableNetworking = true });
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("VirtualMachine").GetProperty("Devices")
            .TryGetProperty("NetworkAdapters", out _));
        return true;
    });

    [Fact]
    public Task Network_adapter_is_emitted_when_an_endpoint_exists() => WithBuilderAsync(builder =>
    {
        var json = builder.Build(Definition() with { NetworkEndpointId = "endpoint-123" });
        using var doc = JsonDocument.Parse(json);
        var adapter = doc.RootElement.GetProperty("VirtualMachine").GetProperty("Devices")
            .GetProperty("NetworkAdapters").GetProperty("0");
        Assert.Equal("endpoint-123", adapter.GetProperty("EndpointId").GetString());
        return true;
    });
}
