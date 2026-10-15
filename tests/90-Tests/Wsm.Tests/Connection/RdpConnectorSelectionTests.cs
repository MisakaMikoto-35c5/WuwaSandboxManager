using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Connection.RdpHost;
using Wsm.Connection.RdpHost.Internal;
using Wsm.Domain.ValueObjects;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Covers the selectable RDP connector: preference parsing, the delegating
/// session host and the request projection.
/// </summary>
public sealed class RdpConnectorSelectionTests
{
    [Theory]
    [InlineData("wpf", RdpConnectorKind.Wpf)]
    [InlineData("WPF", RdpConnectorKind.Wpf)]
    [InlineData("winui", RdpConnectorKind.WinUi)]
    [InlineData("winui3", RdpConnectorKind.WinUi)]
    [InlineData("WinUI-3", RdpConnectorKind.WinUi)]
    public void Preference_parses_known_values(string value, RdpConnectorKind expected)
    {
        Assert.True(RdpConnectorPreference.TryParse(value, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("mstsc")]
    public void Preference_rejects_unknown_values(string? value)
    {
        Assert.False(RdpConnectorPreference.TryParse(value, out _));
    }

    [Fact]
    public async Task Selecting_host_delegates_to_the_preferred_connector()
    {
        var wpf = new FakeConnectorHost(RdpConnectorKind.Wpf);
        var winui = new FakeConnectorHost(RdpConnectorKind.WinUi);
        var host = new SelectingRdpSessionHost(
            [wpf, winui],
            new FixedPreference(RdpConnectorKind.WinUi),
            new EmptyServiceProvider(),
            NullLogger<SelectingRdpSessionHost>.Instance);

        await host.ShowAsync(Request());

        Assert.Empty(wpf.Requests);
        Assert.Single(winui.Requests);
    }

    [Fact]
    public async Task Selecting_host_falls_back_when_the_preferred_connector_is_absent()
    {
        var wpf = new FakeConnectorHost(RdpConnectorKind.Wpf);
        var host = new SelectingRdpSessionHost(
            [wpf],
            new FixedPreference(RdpConnectorKind.WinUi),
            new EmptyServiceProvider(),
            NullLogger<SelectingRdpSessionHost>.Instance);

        await host.ShowAsync(Request());

        Assert.Single(wpf.Requests);
    }

    [Fact]
    public async Task Selecting_host_fails_when_no_connector_is_registered()
    {
        var host = new SelectingRdpSessionHost(
            [],
            new FixedPreference(RdpConnectorKind.Wpf),
            new EmptyServiceProvider(),
            NullLogger<SelectingRdpSessionHost>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ShowAsync(Request()));
    }

    [Fact]
    public void Request_factory_applies_rail_and_transport()
    {
        var runtime = new SandboxRuntimeInfo
        {
            SandboxId = SandboxInstanceId.New(),
            VmId = Guid.NewGuid(),
            Transport = RdpTransportKind.NamedPipe,
            RailMode = true,
            WindowTitle = "Sandbox",
        };

        var request = RdpSessionRequestFactory.Create(runtime.SandboxId, runtime, SandboxConnectionMode.FullDesktop);

        Assert.Equal(runtime.SandboxId.Value, request.SandboxId);
        Assert.Equal(runtime.NamedPipeEndpoint, request.PipeName);
        Assert.True(request.RemoteApp);
        Assert.Equal(SandboxConnectionMode.RemoteApp, request.Mode);
    }

    private static RdpSessionRequest Request() => new()
    {
        SandboxId = Guid.NewGuid(),
        PipeName = @"\\.\pipe\test",
        VmId = Guid.NewGuid(),
    };

    private sealed class FixedPreference : IRdpConnectorPreference
    {
        public FixedPreference(RdpConnectorKind kind) => Connector = kind;

        public RdpConnectorKind Connector { get; }
    }

    private sealed class FakeConnectorHost : IRdpConnectorHost
    {
        public FakeConnectorHost(RdpConnectorKind kind) => Kind = kind;

        public RdpConnectorKind Kind { get; }

        public List<RdpSessionRequest> Requests { get; } = [];

        public Task ShowAsync(RdpSessionRequest request, IRdpSandboxControl? control, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }

        public void CloseAll()
        {
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
