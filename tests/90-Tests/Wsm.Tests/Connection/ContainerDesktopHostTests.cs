using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Connection.ContainerDesktop;
using Wsm.Connection.ContainerDesktop.Internal;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Hcs.Storage.Internal;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Covers the two things that make the container desktop a <b>replaceable module</b>:
/// the manifest-driven provider selection (<see cref="IContainerDesktopProviderRegistry"/>)
/// and the container-side path contract every part of the channel shares. Also guards
/// the regression that once sent the host to its own <c>C:\wsm</c>.
/// </summary>
public sealed class ContainerDesktopProviderTests
{
    [Theory]
    [InlineData(@"C:\wsm", "wsm")]
    [InlineData(@"c:/wsm", "wsm")]
    [InlineData(@"\wsm\sub", @"wsm\sub")]
    [InlineData("wsm", "wsm")]
    [InlineData(@"C:\", "")]
    public void ToRelative_strips_the_drive_root(string input, string expected)
        => Assert.Equal(expected, ContainerDesktopPaths.ToRelative(input));

    [Fact]
    public void Host_path_keeps_the_container_root()
    {
        const string root = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";

        var combined = ContainerDesktopPaths.ToHostPath(root, ContainerDesktopPaths.MirrorFile);

        // Path.Combine(containerRoot, @"C:\wsm\mirror.shm") would return @"C:\wsm\mirror.shm" -
        // the host's own directory instead of the container's.
        Assert.StartsWith(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\", combined);
        Assert.EndsWith(@"wsm\mirror.shm", combined);
    }

    [Fact]
    public void Explicit_provider_id_from_the_manifest_wins()
    {
        var registry = CreateRegistry();
        var recipe = SandboxRecipe.Default with
        {
            Desktop = new SandboxDesktopPlan
            {
                // A kind that would select the frame buffer, but the id is explicit.
                Kind = ContainerDesktopKind.FrameBuffer,
                Provider = ContainerDesktopProviderIds.WindowMirror,
            },
        };

        Assert.Equal(ContainerDesktopProviderIds.WindowMirror, registry.Resolve(recipe).Id);
    }

    [Fact]
    public void Kind_selects_the_matching_provider()
    {
        var registry = CreateRegistry();
        var recipe = SandboxRecipe.Default with
        {
            Desktop = new SandboxDesktopPlan { Kind = ContainerDesktopKind.WindowMirror },
        };

        Assert.Equal(ContainerDesktopProviderIds.WindowMirror, registry.Resolve(recipe).Id);
    }

    [Fact]
    public void An_unregistered_provider_id_is_rejected()
    {
        var registry = CreateRegistry();
        var recipe = SandboxRecipe.Default with
        {
            Desktop = new SandboxDesktopPlan { Provider = "does-not-exist" },
        };

        var exception = Assert.Throws<SandboxValidationException>(() => registry.Resolve(recipe));
        Assert.Contains("does-not-exist", exception.Message);
    }

    [Fact]
    public void A_disabled_plan_is_rejected()
    {
        var registry = CreateRegistry();

        Assert.Throws<SandboxValidationException>(
            () => registry.Resolve(SandboxRecipe.Default with { Desktop = SandboxDesktopPlan.Off }));
    }

    [Fact]
    public void An_unsupported_provider_is_rejected()
    {
        var registry = CreateRegistry(windowMirrorSupported: false);
        var recipe = SandboxRecipe.Default with
        {
            Desktop = new SandboxDesktopPlan { Kind = ContainerDesktopKind.WindowMirror },
        };

        Assert.Throws<SandboxBackendException>(() => registry.Resolve(recipe));
    }

    [Fact]
    public void The_logon_hook_is_appended_to_userinit_only_once()
    {
        var once = ContainerImageCustomizer.AppendUserinit(
            ContainerImageCustomizer.DefaultUserinit, ContainerDesktopPaths.WrapCommand);
        var twice = ContainerImageCustomizer.AppendUserinit(once, ContainerDesktopPaths.WrapCommand);

        Assert.Equal(once, twice);
        Assert.EndsWith(ContainerDesktopPaths.WrapCommand, once);
        Assert.StartsWith(ContainerImageCustomizer.DefaultUserinit, once);
    }

    [Fact]
    public void An_empty_userinit_falls_back_to_the_image_default()
    {
        var value = ContainerImageCustomizer.AppendUserinit(string.Empty, ContainerDesktopPaths.WrapCommand);

        Assert.Equal(
            ContainerImageCustomizer.DefaultUserinit + "," + ContainerDesktopPaths.WrapCommand, value);
    }

    [Theory]
    [InlineData("Wsm.Hcs.Storage.Images.wsm-winlogon-wrap.cmd")]
    [InlineData("Wsm.Hcs.Storage.Images.wsm-desktop-read.cmd")]
    [InlineData("Wsm.Hcs.Storage.Images.wsm-desktop-read.ps1")]
    public void The_window_mirror_payload_is_embedded(string resourceName)
        => Assert.NotNull(typeof(ContainerImageCustomizer).Assembly.GetManifestResourceStream(resourceName));

    private static ContainerDesktopProviderRegistry CreateRegistry(bool windowMirrorSupported = true)
        => new(
            new IContainerDesktopProvider[]
            {
                new StubProvider(ContainerDesktopProviderIds.FrameBuffer, ContainerDesktopKind.FrameBuffer),
                new StubProvider(
                    ContainerDesktopProviderIds.WindowMirror, ContainerDesktopKind.WindowMirror, windowMirrorSupported),
            },
            NullLogger<ContainerDesktopProviderRegistry>.Instance);

    /// <summary>A provider that only reports its identity and availability.</summary>
    private sealed class StubProvider : IContainerDesktopProvider
    {
        public StubProvider(string id, ContainerDesktopKind kind, bool supported = true)
        {
            Id = id;
            Kind = kind;
            IsSupported = supported;
        }

        public string Id { get; }

        public ContainerDesktopKind Kind { get; }

        public bool IsSupported { get; }

        public IReadOnlyList<ContainerDesktopSession> Sessions => Array.Empty<ContainerDesktopSession>();

        public ContainerDesktopSession? Get(SandboxInstanceId id) => null;

        public Task<ContainerDesktopSession> StartAsync(
            ContainerDesktopRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ContainerDesktopSession?> AttachAsync(
            ContainerDesktopRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<ContainerDesktopSession?>(null);

        public Task<ContainerDesktopSession> PresentAsync(
            SandboxInstanceId id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SendInputAsync(
            SandboxInstanceId id, string command, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
