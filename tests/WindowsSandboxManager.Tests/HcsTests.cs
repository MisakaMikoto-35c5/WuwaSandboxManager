using System.Text.Json;
using WindowsSandboxManager.Domain.ValueObjects;
using WindowsSandboxManager.Infrastructure.Hcs;
using Xunit;

namespace WindowsSandboxManager.Tests;

public class HcsSandboxJsonTests
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

    [Fact]
    public void Custom_base_image_and_storage_paths_are_emitted()
    {
        var json = HcsSandboxJson.Build(Definition());
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
    }

    [Fact]
    public void Cpu_and_memory_are_emitted()
    {
        var json = HcsSandboxJson.Build(Definition());
        using var doc = JsonDocument.Parse(json);
        var topology = doc.RootElement.GetProperty("VirtualMachine").GetProperty("ComputeTopology");

        Assert.Equal(12, topology.GetProperty("Processor").GetProperty("Count").GetInt32());
        Assert.Equal(8192u, topology.GetProperty("Memory").GetProperty("SizeInMB").GetUInt32());
    }

    [Fact]
    public void Mapped_folders_become_vsmb_shares()
    {
        var json = HcsSandboxJson.Build(Definition());
        using var doc = JsonDocument.Parse(json);
        var shares = doc.RootElement.GetProperty("VirtualMachine").GetProperty("Devices")
            .GetProperty("VirtualSmb").GetProperty("Shares");

        Assert.Equal(2, shares.GetArrayLength());
        Assert.Equal(@"C:\data", shares[1].GetProperty("Path").GetString());
        Assert.True(shares[1].GetProperty("Options").GetProperty("ReadOnly").GetBoolean());
    }

    [Fact]
    public void Virtual_gpu_can_be_disabled()
    {
        var json = HcsSandboxJson.Build(Definition() with { EnableVirtualGpu = false });
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("VirtualMachine").GetProperty("Devices")
            .TryGetProperty("EnhancedModeVideo", out _));
    }

    [Fact]
    public void Restore_state_is_emitted_for_checkpoints()
    {
        var json = HcsSandboxJson.Build(Definition() with
        {
            TemplateSystemId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        });
        using var doc = JsonDocument.Parse(json);
        var restore = doc.RootElement.GetProperty("VirtualMachine").GetProperty("RestoreState");
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", restore.GetProperty("TemplateSystemId").GetString());
    }

    [Fact]
    public void Hv_socket_exposes_lsm_and_nlm_services()
    {
        var json = HcsSandboxJson.Build(Definition());
        using var doc = JsonDocument.Parse(json);
        var table = doc.RootElement.GetProperty("VirtualMachine").GetProperty("Devices")
            .GetProperty("HvSocket").GetProperty("HvSocketConfig").GetProperty("ServiceTable");
        var ids = table.EnumerateArray().Select(e => e.GetProperty("ServiceId").GetString()).ToArray();
        Assert.Contains(HcsSandboxJson.LsmServiceId.ToString(), ids);
        Assert.Contains(HcsSandboxJson.NlmServiceId.ToString(), ids);
    }
}

public class SandboxStorageOptionsTests
{
    [Fact]
    public void Default_has_no_custom_location()
    {
        Assert.False(SandboxStorageOptions.Default.HasCustomLocation);
    }

    [Fact]
    public void WithPath_sets_custom_location()
    {
        var options = SandboxStorageOptions.Default.WithPath(@"E:\sandboxes");
        Assert.True(options.HasCustomLocation);
        Assert.Equal(@"E:\sandboxes", options.StoragePath);
    }

    [Fact]
    public void Recipe_carries_storage_options()
    {
        var recipe = SandboxRecipe.Default with { Storage = SandboxStorageOptions.Default.WithPath(@"E:\x") };
        Assert.True(recipe.Storage.HasCustomLocation);
    }
}
