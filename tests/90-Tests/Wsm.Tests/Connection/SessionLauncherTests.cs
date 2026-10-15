using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Connection.RailClient;
using Wsm.Connection.RdpHost;
using Wsm.Connection.Session.Internal;
using Wsm.Domain.ValueObjects;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Covers <see cref="SessionLauncher"/> routing: full desktops are presented
/// through the windowed <b>mstscax</b> connector, RemoteApp (RAIL) sessions
/// through the <b>WDAG-style client</b> (dedicated process, RDPBASE named pipe,
/// free-floating application windows).
/// </summary>
public sealed class SessionLauncherTests
{
    [Theory]
    [InlineData(SandboxEngineKind.WindowsSandbox)]
    [InlineData(SandboxEngineKind.HcsSandbox)]
    public async Task Full_desktop_is_presented_through_the_windowed_connector(SandboxEngineKind engine)
    {
        var runtime = Runtime();
        var host = new FakeRdpHost();
        var railClient = new FakeRailClientHost();

        await Launcher(host, railClient)
            .LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop, engine);

        var request = Assert.Single(host.Requests);
        Assert.Equal(runtime.NamedPipeEndpoint, request.PipeName);
        Assert.Equal(runtime.VmId, request.VmId);
        Assert.False(request.RemoteApp);
        Assert.Empty(railClient.Requests);
    }

    [Fact]
    public async Task Remote_app_mode_is_presented_through_the_wdag_style_client()
    {
        var runtime = Runtime();
        var host = new FakeRdpHost();
        var railClient = new FakeRailClientHost();

        await Launcher(host, railClient)
            .LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.RemoteApp, SandboxEngineKind.HcsSandbox);

        Assert.Empty(host.Requests);
        var request = Assert.Single(railClient.Requests);
        Assert.Equal(runtime.SandboxId.Value, request.SandboxId);
        Assert.Equal(runtime.NamedPipeEndpoint, request.PipeName);
        Assert.Null(request.ServerAddress);
        Assert.Equal(runtime.VmId, request.VmId);
        Assert.Equal(SandboxRecipe.DefaultRailProgram, request.Program);
        Assert.Equal("explorer", request.ApplicationName);
    }

    [Fact]
    public async Task A_rail_runtime_opens_the_remote_app_client_even_for_a_full_desktop_request()
    {
        var runtime = Runtime() with { RailMode = true };
        var host = new FakeRdpHost();
        var railClient = new FakeRailClientHost();

        await Launcher(host, railClient)
            .LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop, SandboxEngineKind.HcsSandbox);

        Assert.Empty(host.Requests);
        Assert.Single(railClient.Requests);
    }

    [Fact]
    public async Task The_remote_app_program_prefers_the_per_connect_override()
    {
        var runtime = Runtime() with { RailProgram = @"C:\recipe.exe" };
        var railClient = new FakeRailClientHost();

        await Launcher(new FakeRdpHost(), railClient).LaunchAsync(
            runtime.SandboxId,
            runtime,
            SandboxConnectionMode.RemoteApp,
            SandboxEngineKind.HcsSandbox,
            @"C:\Windows\System32\notepad.exe",
            "--flag");

        var request = Assert.Single(railClient.Requests);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", request.Program);
        Assert.Equal("--flag", request.ProgramArguments);
        Assert.Equal("notepad", request.ApplicationName);
    }

    [Fact]
    public async Task The_recipe_program_is_used_when_no_override_is_given()
    {
        var runtime = Runtime() with { RailProgram = @"C:\recipe.exe" };
        var railClient = new FakeRailClientHost();

        await Launcher(new FakeRdpHost(), railClient).LaunchAsync(
            runtime.SandboxId, runtime, SandboxConnectionMode.RemoteApp, SandboxEngineKind.HcsSandbox);

        Assert.Equal(@"C:\recipe.exe", Assert.Single(railClient.Requests).Program);
    }

    [Fact]
    public async Task A_tcp_rail_runtime_is_presented_over_tcp()
    {
        var runtime = Runtime() with
        {
            Transport = RdpTransportKind.Tcp,
            IpAddress = "172.21.80.101",
            IpAddresses = ["172.21.80.101"],
        };
        var railClient = new FakeRailClientHost();

        await Launcher(new FakeRdpHost(), railClient).LaunchAsync(
            runtime.SandboxId, runtime, SandboxConnectionMode.RemoteApp, SandboxEngineKind.HcsSandbox);

        var request = Assert.Single(railClient.Requests);
        Assert.Equal("172.21.80.101", request.ServerAddress);
        Assert.Equal(3389, request.ServerPort);
        Assert.Null(request.PipeName);
    }

    [Fact]
    public async Task Credentials_and_redirections_are_passed_to_the_rail_client()
    {
        var runtime = Runtime() with
        {
            UserName = "WDAGUtilityAccount",
            Password = "s3cret",
            Redirections = SandboxRedirections.Default with { Clipboard = false, Printer = true },
        };
        var railClient = new FakeRailClientHost();

        await Launcher(new FakeRdpHost(), railClient).LaunchAsync(
            runtime.SandboxId, runtime, SandboxConnectionMode.RemoteApp, SandboxEngineKind.HcsSandbox);

        var request = Assert.Single(railClient.Requests);
        Assert.Equal("WDAGUtilityAccount", request.UserName);
        Assert.Equal("s3cret", request.Password);
        Assert.False(request.Redirections.Clipboard);
        Assert.True(request.Redirections.Printer);
    }

    [Fact]
    public async Task Credentials_and_redirections_are_passed_to_the_desktop_host()
    {
        var runtime = Runtime() with
        {
            UserName = "WDAGUtilityAccount",
            Password = "s3cret",
            Redirections = SandboxRedirections.Default with { Clipboard = false, Printer = true },
        };
        var host = new FakeRdpHost();

        await Launcher(host, new FakeRailClientHost())
            .LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop, SandboxEngineKind.HcsSandbox);

        var request = Assert.Single(host.Requests);
        Assert.Equal("WDAGUtilityAccount", request.UserName);
        Assert.Equal("s3cret", request.Password);
        Assert.False(request.Redirections.Clipboard);
        Assert.True(request.Redirections.Printer);
    }

    [Fact]
    public async Task A_tcp_desktop_runtime_is_presented_over_tcp()
    {
        var runtime = Runtime() with
        {
            Transport = RdpTransportKind.Tcp,
            IpAddress = "172.21.80.101",
            IpAddresses = ["172.21.80.101"],
        };
        var host = new FakeRdpHost();

        await Launcher(host, new FakeRailClientHost())
            .LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop, SandboxEngineKind.HcsSandbox);

        var request = Assert.Single(host.Requests);
        Assert.Equal("172.21.80.101", request.ServerAddress);
        Assert.Equal(3389, request.ServerPort);
    }

    [Fact]
    public async Task Throws_when_a_tcp_desktop_runtime_has_no_address()
    {
        var runtime = Runtime() with { Transport = RdpTransportKind.Tcp };
        var host = new FakeRdpHost();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Launcher(host, new FakeRailClientHost()).LaunchAsync(
                runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop, SandboxEngineKind.HcsSandbox));

        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task Throws_when_a_remote_app_runtime_has_neither_address_nor_vm_id()
    {
        var runtime = Runtime() with { VmId = Guid.Empty, RailMode = true };
        var railClient = new FakeRailClientHost();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Launcher(new FakeRdpHost(), railClient).LaunchAsync(
                runtime.SandboxId, runtime, SandboxConnectionMode.RemoteApp, SandboxEngineKind.HcsSandbox));

        Assert.Empty(railClient.Requests);
    }

    [Fact]
    public async Task Close_all_closes_both_connectors()
    {
        var host = new FakeRdpHost();
        var railClient = new FakeRailClientHost();
        var launcher = Launcher(host, railClient);

        launcher.CloseAll();

        Assert.Equal(1, host.CloseAllCalls);
        Assert.Equal(1, railClient.CloseAllCalls);
    }

    private static SessionLauncher Launcher(IRdpSessionHost host, IRailClientSessionHost railClient)
        => new(host, railClient, NullLogger<SessionLauncher>.Instance);

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

        public int CloseAllCalls { get; private set; }

        public Task ShowAsync(RdpSessionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }

        public void CloseAll() => CloseAllCalls++;
    }

    private sealed class FakeRailClientHost : IRailClientSessionHost
    {
        public List<RailClientSessionRequest> Requests { get; } = new();

        public int CloseAllCalls { get; private set; }

        public Task ShowAsync(RailClientSessionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }

        public void CloseAll() => CloseAllCalls++;
    }
}
