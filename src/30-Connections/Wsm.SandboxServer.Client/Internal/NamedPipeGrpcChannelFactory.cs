using System.IO.Pipes;
using System.Security.Principal;
using Grpc.Net.Client;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Host.Environment;

namespace Wsm.SandboxServer.Client.Internal;

/// <summary>
/// Builds a gRPC channel that speaks HTTP/2 over the Windows Sandbox control
/// named pipe (<c>\\.\pipe\wsandbox\&lt;MD5(sid)&gt;</c>). This is exactly how
/// Microsoft's own <c>ClientConnectionFactory</c> reaches the server.
/// </summary>
internal sealed class NamedPipeGrpcChannelFactory : ITransientDependency
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly ISandboxEnvironment _environment;

    public NamedPipeGrpcChannelFactory(ISandboxEnvironment environment)
        => _environment = environment;

    public GrpcChannel CreateChannel()
    {
        var pipeName = _environment.ServerPipeName
                       ?? throw new SandboxNotSupportedException("Unable to determine the Windows Sandbox control pipe name.");

        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (context, cancellationToken) => ConnectAsync(pipeName, cancellationToken),
            ConnectTimeout = ConnectTimeout,
            EnableMultipleHttp2Connections = true,
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(60),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
        };

        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
        });
    }

    private static async ValueTask<Stream> ConnectAsync(string pipeName, CancellationToken cancellationToken)
    {
        var stream = new NamedPipeClientStream(
            ".",
            pipeName,
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
