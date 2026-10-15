using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Wsm.Connection.RemoteApp.Internal;

/// <summary>
/// The endpoint a loopback bridge forwards to: the sandbox's RDP named pipe, or
/// the guest's TCP RDP listener. A fresh connection is made per bridge
/// connection.
/// </summary>
internal interface IBridgeTarget
{
    /// <summary>A human-readable endpoint label for logs.</summary>
    string Description { get; }

    /// <summary>Opens a new byte stream to the target.</summary>
    Task<Stream> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>Forwards to the sandbox RDP named pipe (<c>\\.\pipe\&lt;VMId&gt;</c>).</summary>
internal sealed class NamedPipeBridgeTarget : IBridgeTarget
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ConnectDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly string _pipeName;

    public NamedPipeBridgeTarget(string pipeName)
    {
        const string prefix = @"\\.\pipe\";
        _pipeName = pipeName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? pipeName[prefix.Length..]
            : pipeName;
    }

    public string Description => $@"\\.\pipe\{_pipeName}";

    public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ConnectDeadline;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The pipe is exclusive per instance: while the guest still holds the
            // previous session the connect waits, so it is bounded and retried.
            var pipe = new NamedPipeClientStream(
                ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            try
            {
                await Task.Run(() => pipe.Connect((int)ConnectTimeout.TotalMilliseconds), cancellationToken)
                    .ConfigureAwait(false);
                return pipe;
            }
            catch (TimeoutException) when (DateTime.UtcNow < deadline)
            {
                pipe.Dispose();
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
        }
    }
}

/// <summary>Forwards to the guest's TCP RDP listener.</summary>
internal sealed class TcpBridgeTarget : IBridgeTarget
{
    private readonly string _address;
    private readonly int _port;

    public TcpBridgeTarget(string address, int port)
    {
        _address = address;
        _port = port;
    }

    public string Description => $"{_address}:{_port}";

    public async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(_address, _port, cancellationToken).ConfigureAwait(false);
            return client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Bridges a sandbox RDP endpoint to a loopback TCP port so the stock Remote
/// Desktop client (<c>mstsc.exe</c>), which can only dial TCP, can drive the
/// session. The sandbox's RDP endpoint — named pipe or guest TCP — carries a
/// plain RDP byte stream (TPKT/X.224), the same stream the platform's own client
/// hands to <c>mstscax</c>, so the bridge is a transparent byte pump, not a
/// protocol translator.
/// <para>
/// The bridge keeps accepting connections until it is disposed: the client
/// reconnects (auto-reconnect, or a new connection after a drop) create new TCP
/// connections that are each forwarded to a fresh target connection.
/// </para>
/// </summary>
internal sealed class LoopbackSessionBridge : IDisposable
{
    private readonly IBridgeTarget _target;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _connectionCount;
    private int _activeConnections;

    public LoopbackSessionBridge(IBridgeTarget target, ILogger logger)
    {
        _target = target;
        _logger = logger;
    }

    /// <summary>The loopback port the client must connect to.</summary>
    public int Port { get; private set; }

    /// <summary>Total accepted client connections (reconnects included).</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>True while at least one client connection is being forwarded.</summary>
    public bool HasConnections => Volatile.Read(ref _activeConnections) > 0;

    /// <summary>Starts listening on an ephemeral loopback port.</summary>
    public void Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        _logger.LogInformation(
            "Bridged sandbox RDP endpoint {Target} to loopback port {Port}.", _target.Description, Port);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                // One pump per connection: a client reconnect must not wait for
                // the previous target connection to drain.
                _ = PumpAsync(client, cancellationToken);
            }
        }
        catch (SocketException ex) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "The loopback bridge listener stopped.");
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task PumpAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;
        Interlocked.Increment(ref _connectionCount);
        Interlocked.Increment(ref _activeConnections);

        try
        {
            await using var targetStream = await _target.ConnectAsync(cancellationToken).ConfigureAwait(false);
            using var network = client.GetStream();

            _logger.LogInformation(
                "RemoteApp client connected; forwarding to {Target}.", _target.Description);
            var upstream = targetStream.CopyToAsync(network, cancellationToken);
            var downstream = network.CopyToAsync(targetStream, cancellationToken);
            await Task.WhenAny(upstream, downstream).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The session ended; nothing to report.
        }
        catch (Exception ex)
        {
            // A refused/failed forward only drops this client connection; the
            // listener stays up for the next (re)connect attempt.
            _logger.LogDebug(ex, "RemoteApp bridge connection to {Target} ended.", _target.Description);
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _listener?.Stop();
        }
        catch
        {
            // Best effort: the listener is going away regardless.
        }

        try
        {
            // The accept loop observes the cancellation; do not block disposal on it.
            _acceptLoop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignored: a slow loop must not delay the session teardown.
        }

        _cts.Dispose();
        _logger.LogInformation("Loopback bridge for {Target} stopped.", _target.Description);
    }
}
