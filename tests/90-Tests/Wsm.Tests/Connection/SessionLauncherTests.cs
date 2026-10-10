using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Connection.Legacy;
using Wsm.Connection.RdpBase;
using Wsm.Connection.RdpHost;
using Wsm.Connection.Session.Internal;
using Wsm.Domain.ValueObjects;
using Wsm.Host.Environment;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Covers <see cref="SessionLauncher"/> routing and argument construction with
/// fakes, so no live RDP session or real process launch is required.
/// </summary>
public sealed class SessionLauncherTests
{
    [Fact]
    public async Task Full_desktop_prefers_the_platform_cli_connect_by_id()
    {
        var runtime = Runtime();
        var process = new FakeProcessLauncher();
        var launcher = Launcher(
            environment: new FakeEnvironment
            {
                SandboxCliExecutablePath = @"C:\WindowsApps\wsb.exe",
                RemoteSessionExecutablePath = @"C:\client.exe",
            },
            process: process);

        await launcher.LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop);

        var startInfo = Assert.Single(process.Launched);
        Assert.Equal(@"C:\WindowsApps\wsb.exe", startInfo.FileName);
        Assert.Equal($@"connect --id {runtime.SandboxId}", startInfo.Arguments);
        // The bogus .rdp/--config path must never come back.
        Assert.DoesNotContain("--config", startInfo.Arguments);
        Assert.DoesNotContain(".rdp", startInfo.Arguments);
    }

    [Fact]
    public async Task Full_desktop_uses_the_packaged_client_with_id_when_no_cli_is_available()
    {
        var runtime = Runtime();
        var process = new FakeProcessLauncher();
        var launcher = Launcher(
            environment: new FakeEnvironment { RemoteSessionExecutablePath = @"C:\client.exe" },
            process: process);

        await launcher.LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop);

        var startInfo = Assert.Single(process.Launched);
        Assert.Equal(@"C:\client.exe", startInfo.FileName);
        Assert.Equal($@"--id {runtime.SandboxId}", startInfo.Arguments);
    }

    [Fact]
    public async Task Full_desktop_uses_the_legacy_launcher_when_no_packaged_client_exists()
    {
        var runtime = Runtime();
        var legacy = new FakeLegacyLauncher();
        var process = new FakeProcessLauncher();
        var launcher = Launcher(
            environment: new FakeEnvironment(),
            process: process,
            legacy: legacy);

        await launcher.LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop);

        Assert.Empty(process.Launched);
        Assert.Equal(runtime.SandboxId, legacy.LastId);
        Assert.NotNull(legacy.LastRecipe);
        Assert.False(legacy.LastRecipe!.RailMode);
    }

    [Fact]
    public async Task Remote_app_acquires_the_pipe_endpoint_and_presents_it_through_the_host()
    {
        var runtime = Runtime() with { RailMode = true, Transport = RdpTransportKind.NamedPipe };
        var endpoint = new object();
        var connector = new FakeConnector(endpoint);
        var factory = new FakeRdpFactory(connector);
        var host = new FakeRdpHost();
        var process = new FakeProcessLauncher();
        var launcher = Launcher(
            environment: new FakeEnvironment { RemoteSessionExecutablePath = @"C:\client.exe" },
            process: process,
            factory: factory,
            host: host);

        await launcher.LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.RemoteApp);

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(runtime.NamedPipeEndpoint, connector.LastPipeName);
        var request = Assert.Single(host.Requests);
        Assert.Same(endpoint, request.Endpoint);
        Assert.True(request.RemoteApp);
        Assert.Empty(process.Launched);
    }

    [Fact]
    public async Task Remote_app_falls_back_to_the_platform_cli_when_the_direct_path_fails()
    {
        var runtime = Runtime() with { RailMode = true, Transport = RdpTransportKind.NamedPipe };
        var factory = new FakeRdpFactory(new FakeConnector(new object(), throwOnConnect: true));
        var process = new FakeProcessLauncher();
        var launcher = Launcher(
            environment: new FakeEnvironment
            {
                SandboxCliExecutablePath = @"C:\WindowsApps\wsb.exe",
                RemoteSessionExecutablePath = @"C:\client.exe",
            },
            process: process,
            factory: factory,
            host: new FakeRdpHost());

        await launcher.LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.RemoteApp);

        var startInfo = Assert.Single(process.Launched);
        Assert.Equal(@"C:\WindowsApps\wsb.exe", startInfo.FileName);
        Assert.Equal($@"connect --id {runtime.SandboxId}", startInfo.Arguments);
    }

    [Fact]
    public async Task Remote_app_falls_back_to_the_client_id_when_no_cli_is_available()
    {
        var runtime = Runtime() with { RailMode = true, Transport = RdpTransportKind.NamedPipe };
        var factory = new FakeRdpFactory(new FakeConnector(new object(), throwOnConnect: true));
        var process = new FakeProcessLauncher();
        var launcher = Launcher(
            environment: new FakeEnvironment { RemoteSessionExecutablePath = @"C:\client.exe" },
            process: process,
            factory: factory,
            host: new FakeRdpHost());

        await launcher.LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.RemoteApp);

        var startInfo = Assert.Single(process.Launched);
        Assert.Equal($@"--id {runtime.SandboxId}", startInfo.Arguments);
    }

    [Fact]
    public async Task Remote_app_falls_back_to_legacy_when_no_packaged_client_exists()
    {
        var runtime = Runtime() with { RailMode = true, Transport = RdpTransportKind.NamedPipe };
        var legacy = new FakeLegacyLauncher();
        var process = new FakeProcessLauncher();
        var launcher = Launcher(
            environment: new FakeEnvironment(),
            process: process,
            factory: new FakeRdpFactory(new FakeConnector(new object(), throwOnConnect: true)),
            host: new FakeRdpHost(),
            legacy: legacy);

        await launcher.LaunchAsync(runtime.SandboxId, runtime, SandboxConnectionMode.RemoteApp);

        Assert.Empty(process.Launched);
        Assert.Equal(runtime.SandboxId, legacy.LastId);
        Assert.True(legacy.LastRecipe!.RailMode);
    }

    private static SessionLauncher Launcher(
        ISandboxEnvironment environment,
        FakeProcessLauncher process,
        ILegacySandboxLauncher? legacy = null,
        IRdpBaseConnectorFactory? factory = null,
        IRdpSessionHost? host = null)
        => new(
            environment,
            legacy ?? new FakeLegacyLauncher(),
            factory ?? new FakeRdpFactory(new FakeConnector(new object())),
            host ?? new FakeRdpHost(),
            process,
            NullLogger<SessionLauncher>.Instance);

    private static SandboxRuntimeInfo Runtime() => new()
    {
        SandboxId = SandboxInstanceId.New(),
        VmId = Guid.NewGuid(),
        Transport = RdpTransportKind.NamedPipe,
        WindowTitle = "Windows Sandbox",
        UserName = "WDAGUtilityAccount",
        Password = string.Empty,
    };

    private sealed class FakeEnvironment : ISandboxEnvironment
    {
        public bool IsSupported => true;

        public bool IsElevated => true;

        public bool IsMultipleInstancesAllowed => false;

        public string? PackageInstallPath => null;

        public string? ServerPipeName => "wsandbox/test";

        public string? LegacyExecutablePath { get; set; } = @"C:\Windows\System32\WindowsSandbox.exe";

        public string? RemoteSessionExecutablePath { get; set; }

        public string? SandboxCliExecutablePath { get; set; }

        public string? ServerExecutablePath => null;

        public bool EnsureServerRunning() => true;
    }

    private sealed class FakeLegacyLauncher : ILegacySandboxLauncher
    {
        public SandboxInstanceId? LastId { get; private set; }

        public SandboxRecipe? LastRecipe { get; private set; }

        public void Launch(SandboxInstanceId id, SandboxRecipe recipe)
        {
            LastId = id;
            LastRecipe = recipe;
        }
    }

    private sealed class FakeRdpFactory : IRdpBaseConnectorFactory
    {
        private readonly IRdpBaseConnector _connector;

        public FakeRdpFactory(IRdpBaseConnector connector) => _connector = connector;

        public int CreateCount { get; private set; }

        public IRdpBaseConnector Create()
        {
            CreateCount++;
            return _connector;
        }
    }

    private sealed class FakeConnector : IRdpBaseConnector
    {
        private readonly object _endpoint;
        private readonly bool _throwOnConnect;

        public FakeConnector(object endpoint, bool throwOnConnect = false)
        {
            _endpoint = endpoint;
            _throwOnConnect = throwOnConnect;
        }

        public string? LastPipeName { get; private set; }

        public Task<object> GetEndpointAsync(string pipeName, CancellationToken cancellationToken = default)
        {
            LastPipeName = pipeName;
            return _throwOnConnect
                ? Task.FromException<object>(new InvalidOperationException("RDPBASE connect failed."))
                : Task.FromResult(_endpoint);
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeRdpHost : IRdpSessionHost
    {
        public List<RdpSessionRequest> Requests { get; } = new();

        public Task ShowAsync(RdpSessionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeProcessLauncher : ISessionProcessLauncher
    {
        public List<ProcessStartInfo> Launched { get; } = new();

        public void Launch(ProcessStartInfo startInfo) => Launched.Add(startInfo);
    }
}
