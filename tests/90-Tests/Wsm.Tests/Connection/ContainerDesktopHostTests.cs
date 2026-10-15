using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
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

    [Fact]
    public void The_mirror_layout_round_trips()
    {
        // The layout is the contract between the read side (PowerShell, inside the
        // container) and the host readers - pin it down.
        var geometry = new ContainerDesktopGeometry(64, 32, 64 * 4, 4);
        var buffer = new byte[ContainerDesktopMirror.Capacity(geometry)];

        Write(buffer, 0, ContainerDesktopMirror.Magic);
        Write(buffer, 4, 1);
        Write(buffer, 8, geometry.Width);
        Write(buffer, 12, geometry.Height);
        Write(buffer, 16, geometry.Stride);
        Write(buffer, 20, geometry.Fps);
        WriteLong(buffer, 24, 7L);
        Write(buffer, 40, 2);
        var table = ContainerDesktopMirror.PixelOffset + (geometry.Stride * geometry.Height);
        WriteLong(buffer, table, 0x1234L);
        Write(buffer, table + 8, 11);
        Write(buffer, table + 12, 22);
        Write(buffer, table + 16, 33);
        Write(buffer, table + 20, 44);
        WriteLong(buffer, table + 24, 7L);
        Write(buffer, table + 32, 0);
        Write(buffer, table + 36, 5);
        var first = Encoding.Unicode.GetBytes("First");
        first.CopyTo(buffer.AsSpan(table + ContainerDesktopMirror.WindowTableBytes));

        var second = table + ContainerDesktopMirror.WindowEntrySize;
        WriteLong(buffer, second, 0x5678L);
        Write(buffer, second + 8, 55);
        Write(buffer, second + 12, 66);
        Write(buffer, second + 16, 77);
        Write(buffer, second + 20, 88);
        WriteLong(buffer, second + 24, 7L);
        Write(buffer, second + 32, ContainerDesktopMirror.TitleSlotBytes);
        Write(buffer, second + 36, 6);
        var secondTitle = Encoding.Unicode.GetBytes("Second");
        secondTitle.CopyTo(buffer.AsSpan(table + ContainerDesktopMirror.WindowTableBytes + ContainerDesktopMirror.TitleSlotBytes));

        var path = Path.Combine(Path.GetTempPath(), $"wsm-mirror-{Guid.NewGuid():N}.shm");
        try
        {
            File.WriteAllBytes(path, buffer);

            var read = ContainerDesktopMirror.TryReadHeader(path, out var count, out var sequence);
            Assert.NotNull(read);
            Assert.Equal(geometry, read!.Value);
            Assert.Equal(2, count);
            Assert.Equal(7L, sequence);

            var windows = ContainerDesktopMirror.ReadWindows(path, read.Value, count);
            Assert.Equal(2, windows.Count);
            Assert.Equal("First", windows[0].Title);
            Assert.Equal(0x1234L, windows[0].Id);
            Assert.Equal((11, 22, 33, 44), (windows[0].X, windows[0].Y, windows[0].Width, windows[0].Height));
            Assert.Equal("Second", windows[1].Title);
            Assert.Equal((55, 66, 77, 88), (windows[1].X, windows[1].Y, windows[1].Width, windows[1].Height));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_foreign_file_is_not_mistaken_for_a_mirror()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wsm-mirror-{Guid.NewGuid():N}.shm");
        try
        {
            File.WriteAllBytes(path, new byte[ContainerDesktopMirror.PixelOffset]);

            Assert.Null(ContainerDesktopMirror.TryReadHeader(path, out var count, out _));
            Assert.Equal(0, count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Write(byte[] buffer, int offset, int value)
        => BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteLong(byte[] buffer, int offset, long value)
        => BitConverter.GetBytes(value).CopyTo(buffer, offset);

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
