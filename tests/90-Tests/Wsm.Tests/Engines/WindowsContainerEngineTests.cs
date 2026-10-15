using Wsm.Domain.Exceptions;
using Wsm.Engine.WindowsContainer;
using Xunit;

namespace Wsm.Tests.Engines;

/// <summary>
/// Covers the pure helpers of the Windows container engine: extracting the layer
/// id from a registered layer path (for the HCS <c>Layers[].Id</c>) and producing
/// a DNS-safe host name from the sandbox name.
/// </summary>
public sealed class WindowsContainerEngineTests
{
    [Theory]
    [InlineData(
        @"C:\ProgramData\Microsoft\Windows\Containers\Layers\aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\Files",
        "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData(
        @"D:\custom\Layers\01234567-89AB-CDEF-0123-456789ABCDEF\Files",
        "01234567-89AB-CDEF-0123-456789ABCDEF")]
    [InlineData(
        @"C:\ProgramData\Microsoft\Windows\Containers\BaseImages\1234\BaseLayer\Files",
        null)]
    public void TryGetLayerId_extracts_the_guid_when_the_path_embeds_one(string path, string? expected)
    {
        Assert.Equal(expected, WindowsContainerEngine.TryGetLayerId(path));
    }

    [Fact]
    public void ToHostName_is_a_short_ascii_label()
    {
        var host = WindowsContainerEngine.ToHostName(
            "Demo Sandbox", "11111111-2222-3333-4444-555555555555");

        Assert.Equal("DemoSandbox-111", host);
        Assert.True(host.Length <= 15);
        Assert.All(host, ch => Assert.True(char.IsAsciiLetterOrDigit(ch) || ch == '-'));
    }

    [Fact]
    public void ToHostName_falls_back_when_the_name_has_no_ascii_characters()
    {
        var host = WindowsContainerEngine.ToHostName(
            "沙箱", "11111111-2222-3333-4444-555555555555");

        Assert.Equal("sandbox-1111111", host);
    }

    [Fact]
    public void EndpointName_is_deterministic_and_id_based()
    {
        var id = Wsm.Domain.ValueObjects.SandboxInstanceId.From(
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        var name = WindowsContainerEngine.EndpointName(id);

        Assert.Equal("wsm-container-aaaaaaaabbbbccccddddeeeeeeeeeeee", name);
        Assert.Equal(name, WindowsContainerEngine.EndpointName(id));
    }
}
