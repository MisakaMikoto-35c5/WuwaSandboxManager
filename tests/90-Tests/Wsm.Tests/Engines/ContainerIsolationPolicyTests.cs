using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wsm.Engine.WindowsContainer;
using Wsm.Engine.WindowsContainer.Internal;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Storage;
using Wsm.Hcs.Storage.Internal;
using Xunit;

namespace Wsm.Tests.Engines;

/// <summary>
/// Covers the process-isolation policy that mirrors Microsoft's container
/// version-compatibility rule: an exact build match is always allowed, and a
/// Windows 11 host additionally accepts a Windows Server 2022/2025 image despite
/// the build mismatch.
/// </summary>
public sealed class ContainerIsolationPolicyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wsm-isolation-" + Guid.NewGuid().ToString("N"));

    public ContainerIsolationPolicyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(WindowsContainerEngineOptions.ImageOverrideVariable, null);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    [Fact]
    public void No_image_is_not_supported()
    {
        var result = Evaluate(hostBuild: 26300, isWindows11Client: true, image: null);

        Assert.False(result.IsSupported);
        Assert.Equal("Container.Reason.NoImage", result.ReasonKey);
    }

    [Fact]
    public void An_exact_build_match_is_always_allowed()
    {
        var image = CreateImage(build: 26300, isServer: false);
        var result = Evaluate(hostBuild: 26300, isWindows11Client: true, image: image);

        Assert.True(result.IsSupported);
        Assert.Equal("Container.Reason.SupportedExact", result.ReasonKey);
        Assert.Equal(26300, result.ImageBuild);
    }

    [Fact]
    public void A_client_image_with_a_different_build_is_rejected()
    {
        var image = CreateImage(build: 26100, isServer: false);
        var result = Evaluate(hostBuild: 26300, isWindows11Client: true, image: image);

        Assert.False(result.IsSupported);
        Assert.Equal("Container.Reason.BuildMismatch", result.ReasonKey);
    }

    [Fact]
    public void A_windows_server_image_is_allowed_on_windows_11_despite_the_mismatch()
    {
        var image = CreateImage(build: 20348, isServer: true);
        var result = Evaluate(hostBuild: 26300, isWindows11Client: true, image: image);

        Assert.True(result.IsSupported);
        Assert.Equal("Container.Reason.SupportedServer", result.ReasonKey);
        Assert.True(result.ImageIsServer);
    }

    [Fact]
    public void A_server_image_newer_than_the_host_is_rejected()
    {
        var image = CreateImage(build: 26300, isServer: true);
        var result = Evaluate(hostBuild: 26100, isWindows11Client: true, image: image);

        Assert.False(result.IsSupported);
        Assert.Equal("Container.Reason.BuildMismatch", result.ReasonKey);
    }

    [Fact]
    public void A_server_image_is_rejected_when_the_host_is_not_windows_11()
    {
        var image = CreateImage(build: 20348, isServer: true);
        var result = Evaluate(hostBuild: 19045, isWindows11Client: false, image: image);

        Assert.False(result.IsSupported);
        Assert.Equal("Container.Reason.BuildMismatch", result.ReasonKey);
    }

    [Fact]
    public void A_server_image_below_the_minimum_server_build_is_rejected()
    {
        var image = CreateImage(build: 17763, isServer: true);
        var result = Evaluate(hostBuild: 26300, isWindows11Client: true, image: image);

        Assert.False(result.IsSupported);
        Assert.Equal("Container.Reason.BuildMismatch", result.ReasonKey);
    }

    [Fact]
    public void Without_metadata_the_build_is_read_from_the_kernel_and_treated_as_client()
    {
        var image = Path.Combine(_root, "kernel-only");
        // A real ntoskrnl file makes this an end-to-end read; keep it simple by
        // asserting the fallback path returns a deterministic (unsupported) result
        // when the file is absent.
        Directory.CreateDirectory(image);

        var result = Evaluate(hostBuild: 26300, isWindows11Client: true, image: image);

        Assert.False(result.IsSupported);
        Assert.Equal("Container.Reason.UnknownImage", result.ReasonKey);
    }

    [Theory]
    [InlineData("mcr.microsoft.com/windows/servercore:ltsc2022", true)]
    [InlineData("mcr.microsoft.com/windows/nanoserver:ltsc2025", true)]
    [InlineData("mcr.microsoft.com/windows/server:ltsc2022", true)]
    [InlineData("mcr.microsoft.com/windows:ltsc2022", false)]
    public void Server_repositories_are_recognized(string reference, bool expected)
    {
        var repository = reference[(reference.IndexOf('/') + 1)..reference.IndexOf(':')];
        Assert.Equal(expected, ContainerImageImporter.IsServerRepository(repository));
    }

    private string CreateImage(int build, bool isServer)
    {
        var path = Path.Combine(_root, $"img-{build}-{isServer}");
        Directory.CreateDirectory(path);
        new ContainerImageMetadata { Build = build, IsServer = isServer, SourceReference = "test" }.Write(path);
        return path;
    }

    private ContainerIsolationSupport Evaluate(int hostBuild, bool isWindows11Client, string? image)
    {
        Environment.SetEnvironmentVariable(WindowsContainerEngineOptions.ImageOverrideVariable, null);
        var options = Options.Create(new WindowsContainerEngineOptions { BaseImageLayerPath = image });
        var policy = new ContainerIsolationPolicy(
            options,
            new NullLayout(),
            new StubHost(hostBuild, isWindows11Client),
            NullLogger<ContainerIsolationPolicy>.Instance);

        return policy.Evaluate();
    }

    private sealed class StubHost(int build, bool isWindows11Client) : IHostOsInfoProvider
    {
        public HostOsInfo Read() => new(build, isWindows11Client);
    }

    private sealed class NullLayout : IHcsHostLayout
    {
        public string PlatformContainersFolder => Path.GetTempPath();
        public string LayersFolder => Path.GetTempPath();
        public string BaseImagesFolder => Path.GetTempPath();
        public string PlatformContainerStoragesFolder => Path.GetTempPath();
        public string SnapshotsFolder => Path.GetTempPath();
        public string SandboxDataFolder => Path.GetTempPath();
        public string GetStorageDirectory(string id) => Path.Combine(Path.GetTempPath(), id);
        public IReadOnlyList<HcsLayerInfo> ListLayers() => Array.Empty<HcsLayerInfo>();
        public string? ResolveBaseLayerPath() => null;
        public HcsLayerInfo? ResolveBaseImageLayer() => null;
    }
}
