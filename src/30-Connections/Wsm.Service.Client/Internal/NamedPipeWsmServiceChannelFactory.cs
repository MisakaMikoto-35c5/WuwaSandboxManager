using System.IO.Pipes;
using System.Security.Principal;
using Grpc.Net.Client;
using Volo.Abp.DependencyInjection;

namespace Wsm.Service.Client.Internal;

/// <summary>
/// Builds - and caches for the process lifetime - the single gRPC channel that
/// speaks HTTP/2 over the WSM agent control named pipe
/// (<c>\\.\pipe\wsm-agent</c>). The address is a dummy HTTP URL; the custom
/// <see cref="SocketsHttpHandler.ConnectCallback"/> dials the pipe instead of a
/// TCP socket.
/// </summary>
internal sealed class NamedPipeWsmServiceChannelFactory : ISingletonDependency
{
    /// <summary>The fixed control pipe exposed by the WSM agent service.</summary>
    public const string PipeName = "wsm-agent";

    /// <summary>
    /// Fallback deadline for a service call that does not specify its own. In
    /// practice every call site passes a per-RPC deadline (see
    /// <c>WsmServiceClient</c>), so this only guards accidental omissions.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Short deadline used by the <c>IsAvailable</c> probe.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly Lazy<GrpcChannel> _channel;

    public NamedPipeWsmServiceChannelFactory()
        => _channel = new Lazy<GrpcChannel>(CreateChannel, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The cached, process-wide channel used by every call.</summary>
    public GrpcChannel Channel => _channel.Value;

    private static GrpcChannel CreateChannel()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (context, cancellationToken) => ConnectAsync(cancellationToken),
            ConnectTimeout = ConnectTimeout,
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
        };

        // The scheme is a lie (the ConnectCallback dials the pipe, not a TCP
        // socket), but it is load-bearing: "https" makes the handler try to wrap
        // the plaintext pipe stream in TLS, so every RPC fails. "http" keeps the
        // stream plaintext, matching NamedPipeGrpcChannelFactory's prior art.
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        });
    }

    private static async ValueTask<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        var stream = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.InOut,
            PipeOptions.WriteThrough | PipeOptions.Asynchronous,
            TokenImpersonationLevel.Anonymous);

        try
        {
            await stream.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
