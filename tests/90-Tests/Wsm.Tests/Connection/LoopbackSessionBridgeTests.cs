using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Connection.RemoteApp.Internal;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Covers the loopback bridge that lets the stock Remote Desktop client
/// (<c>mstsc.exe</c>, TCP only) drive an RDP endpoint the client cannot dial
/// itself — the sandbox named pipe first of all. The bridge must be a
/// transparent byte pump.
/// </summary>
public sealed class LoopbackSessionBridgeTests
{
    [Fact]
    public async Task The_bridge_forwards_bytes_between_tcp_and_a_named_pipe()
    {
        var pipeName = $"wsm-bridge-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var accept = server.WaitForConnectionAsync();

        using var bridge = new LoopbackSessionBridge(
            new NamedPipeBridgeTarget($@"\\.\pipe\{pipeName}"), NullLogger.Instance);
        bridge.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, bridge.Port);
        await accept.WaitAsync(TimeSpan.FromSeconds(10));
        var network = client.GetStream();

        // client -> pipe
        var request = Encoding.ASCII.GetBytes("ping-request");
        await network.WriteAsync(request);
        var received = new byte[request.Length];
        await server.ReadExactlyAsync(received);
        Assert.Equal(request, received);

        // pipe -> client
        var response = Encoding.ASCII.GetBytes("pong-response");
        await server.WriteAsync(response);
        await server.FlushAsync();
        var echoed = new byte[response.Length];
        await network.ReadExactlyAsync(echoed);
        Assert.Equal(response, echoed);
    }

    [Fact]
    public async Task The_bridge_forwards_bytes_between_tcp_and_a_tcp_target()
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var targetPort = ((IPEndPoint)target.LocalEndpoint).Port;

        using var bridge = new LoopbackSessionBridge(
            new TcpBridgeTarget("127.0.0.1", targetPort), NullLogger.Instance);
        bridge.Start();

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, bridge.Port);
        using var upstream = await target.AcceptTcpClientAsync();

        var network = client.GetStream();
        var upstreamStream = upstream.GetStream();

        var payload = Encoding.ASCII.GetBytes("rail-bytes");
        await network.WriteAsync(payload);
        var received = new byte[payload.Length];
        await upstreamStream.ReadExactlyAsync(received);
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task The_bridge_reports_connections_and_reconnect_ability()
    {
        var pipeName = $"wsm-bridge-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var accept = server.WaitForConnectionAsync();

        using var bridge = new LoopbackSessionBridge(
            new NamedPipeBridgeTarget(pipeName), NullLogger.Instance);
        bridge.Start();

        using var first = new TcpClient();
        await first.ConnectAsync(IPAddress.Loopback, bridge.Port);
        await accept.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(await WaitForAsync(() => bridge.ConnectionCount >= 1));
        Assert.True(await WaitForAsync(() => bridge.HasConnections));

        // Dropping the client ends the forwarded connection; the listener stays up.
        first.Close();
        Assert.True(await WaitForAsync(() => !bridge.HasConnections));
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }
}
