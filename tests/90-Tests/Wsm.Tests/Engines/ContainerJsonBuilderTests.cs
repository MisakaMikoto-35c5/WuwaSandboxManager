using System.Text.Json;
using Wsm.Hcs.Json.Container;
using Wsm.Hcs.Json.Container.Internal;
using Xunit;

namespace Wsm.Tests.Engines;

/// <summary>
/// JSON tests for the Windows container configuration builder: it must reuse the
/// Windows Sandbox base layer as a read-only container layer (with its layer id),
/// emit the resource limits and mappings, and omit optional sections that carry
/// no data.
/// </summary>
public sealed class ContainerJsonBuilderTests
{
    private static readonly IContainerJsonBuilder Builder = new ContainerJsonBuilder();

    private const string BaseLayerId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    private static ContainerDefinition Definition() => new()
    {
        Id = "11111111-2222-3333-4444-555555555555",
        BaseLayerPath = @"C:\ProgramData\Microsoft\Windows\Containers\Layers\" + BaseLayerId + @"\Files",
        BaseLayerId = BaseLayerId,
        ScratchPath = @"E:\wsm\11111111-2222-3333-4444-555555555555\volume",
        HostName = "wsm-container",
        MemoryMb = 4096,
        CpuCount = 4,
        MappedFolders =
        [
            new ContainerMappedFolder(@"C:\data", @"C:\share", ReadOnly: true),
        ],
        EnableNetworking = true,
        NetworkAdapterIds = ["22222222-2222-2222-2222-222222222222"],
    };

    [Fact]
    public void Shared_base_layer_is_emitted_as_a_container_layer_with_its_id()
    {
        var json = Builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);

        var container = doc.RootElement.GetProperty("Container");
        var storage = container.GetProperty("Storage");

        Assert.Equal(
            @"E:\wsm\11111111-2222-3333-4444-555555555555\volume",
            storage.GetProperty("Path").GetString());

        var layer = storage.GetProperty("Layers")[0];
        Assert.Equal(BaseLayerId, layer.GetProperty("Id").GetString());
        Assert.Equal(
            @"C:\ProgramData\Microsoft\Windows\Containers\Layers\" + BaseLayerId + @"\Files",
            layer.GetProperty("Path").GetString());
    }

    [Fact]
    public void Container_terminates_with_the_last_handle_and_uses_the_host_name()
    {
        var json = Builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("ShouldTerminateOnLastHandleClosed").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("SchemaVersion").GetProperty("Major").GetInt32());
        Assert.Equal("wsm-container", doc.RootElement.GetProperty("Container").GetProperty("GuestOs").GetProperty("HostName").GetString());
    }

    [Fact]
    public void Resource_limits_and_mapped_folders_are_emitted()
    {
        var json = Builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);
        var container = doc.RootElement.GetProperty("Container");

        Assert.Equal(4096UL, container.GetProperty("Memory").GetProperty("SizeInMB").GetUInt64());
        Assert.Equal(4u, container.GetProperty("Processor").GetProperty("Count").GetUInt32());

        var mapped = container.GetProperty("MappedDirectories")[0];
        Assert.Equal(@"C:\data", mapped.GetProperty("HostPath").GetString());
        Assert.Equal(@"C:\share", mapped.GetProperty("ContainerPath").GetString());
        Assert.True(mapped.GetProperty("ReadOnly").GetBoolean());
    }

    [Fact]
    public void Networking_is_emitted_when_an_endpoint_is_supplied()
    {
        var json = Builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);

        var adapters = doc.RootElement.GetProperty("Container").GetProperty("Networking").GetProperty("NetworkAdapters");
        Assert.Equal("22222222-2222-2222-2222-222222222222", adapters[0].GetString());
    }

    [Fact]
    public void Optional_sections_are_omitted_when_empty()
    {
        var definition = Definition() with
        {
            HostName = null,
            BaseLayerId = null,
            MemoryMb = 0,
            CpuCount = 0,
            MappedFolders = [],
            EnableNetworking = false,
            NetworkAdapterIds = [],
        };

        var json = Builder.Build(definition);
        using var doc = JsonDocument.Parse(json);
        var container = doc.RootElement.GetProperty("Container");

        // The host name falls back to the id; the layer id is omitted when
        // unknown; the resource/mapping/networking objects are not present at all
        // (some HCS builds reject empty objects).
        Assert.Equal("11111111-2222-3333-4444-555555555555", container.GetProperty("GuestOs").GetProperty("HostName").GetString());
        Assert.False(container.GetProperty("Storage").GetProperty("Layers")[0].TryGetProperty("Id", out _));
        Assert.False(container.TryGetProperty("Memory", out _));
        Assert.False(container.TryGetProperty("Processor", out _));
        Assert.False(container.TryGetProperty("MappedDirectories", out _));
        Assert.False(container.TryGetProperty("Networking", out _));
    }
}
