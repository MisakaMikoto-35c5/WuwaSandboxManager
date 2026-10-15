using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wsm.Connection.ContainerDesktop;
using Wsm.Connection.ContainerDesktop.Internal;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Covers the container desktop host: its path handling around a container root
/// (a <c>\\?\Volume{GUID}</c> volume, where a *rooted* agent directory must not be
/// allowed to replace the root - that bug sent the host to its own <c>C:\wsm</c>)
/// and its gating/registration behaviour.
/// </summary>
public sealed class ContainerDesktopHostTests
{
    [Theory]
    [InlineData(@"C:\wsm", "wsm")]
    [InlineData(@"c:/wsm", "wsm")]
    [InlineData(@"\wsm\sub", @"wsm\sub")]
    [InlineData("wsm", "wsm")]
    [InlineData(@"C:\", "")]
    public void ToRelativeContainerPath_strips_the_drive_root(string input, string expected)
        => Assert.Equal(expected, ContainerDesktopHost.ToRelativeContainerPath(input));

    [Fact]
    public void Relative_agent_directory_keeps_the_container_root()
    {
        const string root = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\";

        var combined = Path.Combine(
            root.TrimEnd('\\'),
            ContainerDesktopHost.ToRelativeContainerPath(@"C:\wsm"));

        // Path.Combine(containerRoot, "C:\\wsm") would return "C:\\wsm" - i.e. the
        // host's own directory instead of the container's.
        Assert.StartsWith(@"\\?\Volume{11111111-2222-3333-4444-555555555555}\", combined);
        Assert.EndsWith(@"wsm", combined);
    }

    [Fact]
    public void Start_is_refused_when_the_capability_is_disabled()
    {
        var host = CreateHost(enabled: false);

        Assert.False(host.IsSupported);
        Assert.Throws<SandboxBackendException>(
            () => host.StartAsync(SandboxInstanceId.New()).GetAwaiter().GetResult());
    }

    [Fact]
    public void Start_reports_a_missing_container_engine()
    {
        var host = CreateHost(enabled: true);

        var exception = Assert.Throws<SandboxBackendException>(
            () => host.StartAsync(SandboxInstanceId.New()).GetAwaiter().GetResult());
        Assert.Contains("Windows container engine", exception.Message);
    }

    [Fact]
    public async Task Stop_without_a_session_completes()
    {
        var host = CreateHost(enabled: true);

        await host.StopAsync(SandboxInstanceId.New());

        Assert.Empty(host.Sessions);
        Assert.Null(host.Get(SandboxInstanceId.New()));
    }

    private static ContainerDesktopHost CreateHost(bool enabled)
        => new(
            new StubEngineRegistry(),
            new StubLauncher(),
            Options.Create(new ContainerDesktopOptions { Enabled = enabled }),
            NullLogger<ContainerDesktopHost>.Instance);

    private sealed class StubEngineRegistry : ISandboxEngineRegistry
    {
        public IReadOnlyList<ISandboxEngine> Engines => Array.Empty<ISandboxEngine>();

        public ISandboxEngine? Find(SandboxEngineKind kind) => null;
    }

    private sealed class StubLauncher : ISessionProcessLauncher
    {
        public bool CanLaunchIntoInteractiveSession => false;

        public SessionProcessLaunchResult Launch(string commandLine, string? workingDirectory = null)
            => new(false, 0, 0, false, "stub");
    }
}
