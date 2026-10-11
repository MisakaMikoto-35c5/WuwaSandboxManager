using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Connection.RdpHost;
using Wsm.Connection.Session.Internal;
using Wsm.Domain.ValueObjects;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Covers <see cref="SessionLauncher"/>: every engine (Windows Sandbox and HCS)
/// is presented through the <b>custom RDP connector</b> (the RDPBASE named-pipe
/// endpoint + strongly typed mstscax). The packaged Windows Sandbox client is
/// never launched.
/// </summary>
public sealed class SessionLauncherTests
{
    [Theory]
    [InlineData(SandboxEngineKind.WindowsSandbox)]
    [InlineData(SandboxEngineKind.HcsSandbox)]
    public async Task Every_engine_is_presented_through_the_custom_rdp_connector(SandboxEngineKind engine)
    {
        var runtime = Runtime();
        var host = new FakeRdpHost();

        await Launcher(host).LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop, engine);

        var request = Assert.Single(host.Requests);
        Assert.Equal(runtime.NamedPipeEndpoint, request.PipeName);
        Assert.Equal(runtime.VmId, request.VmId);
        Assert.False(request.RemoteApp);
    }

    [Fact]
    public async Task Remote_app_is_requested_for_a_rail_runtime()
    {
        var runtime = Runtime() with { RailMode = true };
        var host = new FakeRdpHost();

        await Launcher(host).LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop, SandboxEngineKind.HcsSandbox);

        Assert.True(Assert.Single(host.Requests).RemoteApp);
    }

    [Fact]
    public async Task Remote_app_mode_is_honoured()
    {
        var runtime = Runtime();
        var host = new FakeRdpHost();

        await Launcher(host).LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.RemoteApp, SandboxEngineKind.HcsSandbox);

        Assert.True(Assert.Single(host.Requests).RemoteApp);
    }

    [Fact]
    public async Task Credentials_and_redirections_are_passed_to_the_host()
    {
        var runtime = Runtime() with
        {
            UserName = "WDAGUtilityAccount",
            Password = "s3cret",
            Redirections = SandboxRedirections.Default with { Clipboard = false, Printer = true },
        };
        var host = new FakeRdpHost();

        await Launcher(host).LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop, SandboxEngineKind.HcsSandbox);

        var request = Assert.Single(host.Requests);
        Assert.Equal("WDAGUtilityAccount", request.UserName);
        Assert.Equal("s3cret", request.Password);
        Assert.False(request.Redirections.Clipboard);
        Assert.True(request.Redirections.Printer);
    }

    [Fact]
    public async Task Throws_when_the_transport_is_not_a_named_pipe()
    {
        var runtime = Runtime() with { Transport = RdpTransportKind.Tcp };
        var host = new FakeRdpHost();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Launcher(host).LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop, SandboxEngineKind.HcsSandbox));

        Assert.Empty(host.Requests);
    }

    private static SessionLauncher Launcher(IRdpSessionHost host)
        => new(host, NullLogger<SessionLauncher>.Instance);

    private static SandboxRuntimeInfo Runtime() => new()
    {
        SandboxId = SandboxInstanceId.New(),
        VmId = Guid.NewGuid(),
        Transport = RdpTransportKind.NamedPipe,
        WindowTitle = "Windows Sandbox",
        UserName = "WDAGUtilityAccount",
        Password = string.Empty,
    };

    private sealed class FakeRdpHost : IRdpSessionHost
    {
        public List<RdpSessionRequest> Requests { get; } = new();

        public Task ShowAsync(RdpSessionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
