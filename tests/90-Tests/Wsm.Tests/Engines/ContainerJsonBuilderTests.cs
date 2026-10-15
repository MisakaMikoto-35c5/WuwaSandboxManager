using System.Text.Json;
using Wsm.Hcs.Json.Container;
using Wsm.Hcs.Json.Container.Internal;
using Xunit;

namespace Wsm.Tests.Engines;

/// <summary>
/// JSON tests for the Windows container configuration builder: it must emit the
/// activated scratch layer's volume mount path as <c>Storage.Path</c>, list the
/// read-only image layers with their GUIDs, attach the HNS namespace for
/// networking, emit the resource limits and mappings, and omit optional sections
/// that carry no data.
/// </summary>
public sealed class ContainerJsonBuilderTests
{
    private static readonly IContainerJsonBuilder Builder = new ContainerJsonBuilder();

    private const string LayerId = "8a7991ea-eb99-5497-8980-b598f0cfb2e8";
    private const string BaseLayerPath = @"C:\images\servercore-ltsc2022";

    private static ContainerDefinition Definition() => new()
    {
        Id = "11111111-2222-3333-4444-555555555555",
        ScratchMountPath = @"\\?\Volume{0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0}\",
        Layers = [new ContainerLayer(LayerId, BaseLayerPath)],
        HostName = "wsm-container",
        MemoryMb = 4096,
        CpuCount = 4,
        NetworkNamespace = "7f348c79-f9ce-4bbd-bb35-e9b17fd5f706",
        MappedFolders =
        [
            new ContainerMappedFolder(@"C:\data", @"C:\share", ReadOnly: true),
        ],
    };

    [Fact]
    public void Scratch_volume_and_read_only_layers_are_emitted()
    {
        var json = Builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);

        var storage = doc.RootElement.GetProperty("Container").GetProperty("Storage");

        Assert.Equal(
            @"\\?\Volume{0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0}\",
            storage.GetProperty("Path").GetString());

        var layer = storage.GetProperty("Layers")[0];
        Assert.Equal(LayerId, layer.GetProperty("Id").GetString());
        Assert.Equal(BaseLayerPath, layer.GetProperty("Path").GetString());
    }

    [Fact]
    public void Container_terminates_with_the_last_handle_and_uses_the_host_name()
    {
        var json = Builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);

        Assert.True(doc.RootElement.GetProperty("ShouldTerminateOnLastHandleClosed").GetBoolean());
        Assert.Equal(2, doc.RootElement.GetProperty("SchemaVersion").GetProperty("Major").GetInt32());
        Assert.Equal(
            "wsm-container",
            doc.RootElement.GetProperty("Container").GetProperty("GuestOs").GetProperty("HostName").GetString());
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
    public void Networking_joins_the_hns_namespace()
    {
        var json = Builder.Build(Definition());
        using var doc = JsonDocument.Parse(json);

        var ns = doc.RootElement.GetProperty("Container")
            .GetProperty("Networking")
            .GetProperty("Namespace")
            .GetString();

        Assert.Equal("7f348c79-f9ce-4bbd-bb35-e9b17fd5f706", ns);
    }

    [Fact]
    public void Optional_sections_are_omitted_when_empty()
    {
        var definition = Definition() with
        {
            HostName = null,
            MemoryMb = 0,
            CpuCount = 0,
            MappedFolders = [],
            NetworkNamespace = null,
        };

        var json = Builder.Build(definition);
        using var doc = JsonDocument.Parse(json);
        var container = doc.RootElement.GetProperty("Container");

        // The host name falls back to the id; the resource/mapping/networking
        // objects are not present at all (some HCS builds reject empty objects).
        Assert.Equal(
            "11111111-2222-3333-4444-555555555555",
            container.GetProperty("GuestOs").GetProperty("HostName").GetString());
        Assert.False(container.TryGetProperty("Memory", out _));
        Assert.False(container.TryGetProperty("Processor", out _));
        Assert.False(container.TryGetProperty("MappedDirectories", out _));
        Assert.False(container.TryGetProperty("Networking", out _));
    }

    [Fact]
    public void Scratch_mount_path_is_required()
    {
        var definition = Definition() with { ScratchMountPath = string.Empty };
        Assert.Throws<ArgumentException>(() => Builder.Build(definition));
    }
}
