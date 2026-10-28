using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace Wsm.Connection.RemoteApp.Internal;

/// <summary>
/// Default <see cref="IRemoteAppSessionHost"/>. Presents a RemoteApp (RAIL)
/// session with the stock Remote Desktop client (<c>mstsc.exe</c>) and a
/// generated <c>.rdp</c> document:
/// <list type="number">
/// <item>the sandbox's RDP endpoint (named pipe or guest TCP) is bridged to an
/// ephemeral loopback port (<see cref="LoopbackSessionBridge"/>);</item>
/// <item>the <c>.rdp</c> is written for that loopback endpoint
/// (<see cref="RemoteAppRdpDocument"/>);</item>
/// <item><c>mstsc.exe</c> is started and tracked until the session ends
/// (<see cref="MstscClientTracker"/> + the bridge's connection state).</item>
/// </list>
/// <para>
/// Because the client runs out of process, the module has no in-process
/// ActiveX/session state to leak: several sandboxes can be presented in
/// parallel, each through its own <c>mstsc</c> process.
/// </para>
/// </summary>
[ExposeServices(typeof(IRemoteAppSessionHost))]
internal sealed class MstscRemoteAppSessionHost : IRemoteAppSessionHost, ISingletonDependency
{
    /// <summary>How long a client may take to appear (or connect) before the launch is treated as aborted.</summary>
    private static readonly TimeSpan ClientDiscoveryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long the session survives without a live connection before it is
    /// treated as ended — the window in which the client auto-reconnects.
    /// </summary>
    private static readonly TimeSpan ReconnectGrace = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a client window gets to close gracefully.</summary>
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    private readonly MstscClientTracker _tracker;
    private readonly ILogger<MstscRemoteAppSessionHost> _logger;

    private readonly object _gate = new();
    private readonly List<LiveSession> _sessions = new();

    /// <summary>Serializes mstsc launches so newly appeared client processes can be attributed.</summary>
    private readonly SemaphoreSlim _launchGate = new(1, 1);

    public MstscRemoteAppSessionHost(ILogger<MstscRemoteAppSessionHost> logger)
        : this(new MstscClientTracker(), logger)
    {
    }

    internal MstscRemoteAppSessionHost(MstscClientTracker tracker, ILogger<MstscRemoteAppSessionHost> logger)
    {
        _tracker = tracker;
        _logger = logger;
    }

    public async Task ShowAsync(RemoteAppSessionRequest request, CancellationToken cancellationToken = default)
    {
        var baseline = _tracker.Snapshot();
        LoopbackSessionBridge? bridge = null;
        LiveSession? session = null;

        await _launchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = BuildTarget(request);
            bridge = new LoopbackSessionBridge(target, _logger);
            bridge.Start();

            // The client always dials the loopback bridge: the named pipe needs
            // it by construction, and routing TCP through it too keeps session
            // lifetime observable (the bridge sees the client's connection).
            var localRequest = request with
            {
                ServerAddress = "127.0.0.1",
                ServerPort = bridge.Port,
                PipeName = null,
            };

            var rdpPath = WriteRdpFile(request, localRequest);
            session = new LiveSession(_tracker, baseline, rdpPath, bridge);
            lock (_gate)
            {
                _sessions.Add(session);
            }

            _logger.LogInformation(
                "Opening RemoteApp session for sandbox {SandboxId} ({Program}) through mstsc over {Target}; client profile '{RdpFile}'.",
                request.SandboxId, request.Program, target.Description, rdpPath);

            _tracker.Launch(rdpPath);
            try
            {
                await WaitForSessionEndAsync(bridge, baseline, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _sessions.Remove(session);
                }

                session.Dispose();
            }
        }
        catch
        {
            session?.Dispose();
            bridge?.Dispose();
            throw;
        }
        finally
        {
            _launchGate.Release();
        }
    }

    public void CloseAll()
    {
        LiveSession[] sessions;
        lock (_gate)
        {
            sessions = _sessions.ToArray();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// Completes when the session is over: the client processes are gone and —
    /// when the client connected — the bridge has been without a live connection
    /// for the reconnect grace window. An aborted launch (no client ever appeared
    /// and nothing ever connected) completes after
    /// <see cref="ClientDiscoveryTimeout"/>.
    /// </summary>
    private async Task WaitForSessionEndAsync(
        LoopbackSessionBridge bridge,
        HashSet<int> baseline,
        CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        DateTime? disconnectedAt = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var clients = _tracker.FindNewClients(baseline);
            foreach (var client in clients)
            {
                // Belt and braces: a client that appeared outside the launcher's
                // job membership is bound here, so killing this host still ends it.
                ProcessJob.Bind(client);
                client.Dispose();
            }

            var connected = bridge.ConnectionCount > 0;

            if (clients.Count == 0)
            {
                if (connected && !bridge.HasConnections)
                {
                    // The client connected and no connection is live: the client
                    // may be auto-reconnecting, so the session only ends once the
                    // reconnect window has passed.
                    disconnectedAt ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - disconnectedAt > ReconnectGrace)
                    {
                        return;
                    }
                }
                else if (!connected && DateTime.UtcNow - started > ClientDiscoveryTimeout)
                {
                    // The launch never produced a client or a connection (the
                    // user cancelled the warning dialog, or mstsc refused the
                    // connection). Nothing to present any more.
                    _logger.LogWarning(
                        "The RemoteApp client for {Target} never connected; giving up.",
                        bridge.Port);
                    return;
                }
            }
            else
            {
                disconnectedAt = null;
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static IBridgeTarget BuildTarget(RemoteAppSessionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ServerAddress))
        {
            return new TcpBridgeTarget(request.ServerAddress!, request.ServerPort);
        }

        if (!string.IsNullOrWhiteSpace(request.PipeName))
        {
            return new NamedPipeBridgeTarget(request.PipeName!);
        }

        throw new InvalidOperationException(
            $"The RemoteApp connector needs either a TCP address or a named-pipe endpoint, " +
            $"but sandbox {request.SandboxId:D} supplied neither.");
    }

    private string WriteRdpFile(RemoteAppSessionRequest request, RemoteAppSessionRequest localRequest)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WsmRemoteApp");
        var name = Sanitize(request.DisplayName);
        var path = Path.Combine(directory, $"{request.SandboxId:N}-{name}.rdp");
        return RemoteAppRdpDocument.Write(localRequest, path);
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value
            .Select(ch => invalid.Contains(ch) ? '_' : ch)
            .ToArray())
            .Trim();

        return string.IsNullOrEmpty(cleaned) ? "app" : cleaned[..Math.Min(cleaned.Length, 40)];
    }

    /// <summary>
    /// A session's live resources. Disposal ends it: the clients beyond the
    /// baseline are closed gracefully (with a kill fallback), the bridge stops
    /// forwarding, and the temporary <c>.rdp</c> document is deleted.
    /// </summary>
    private sealed class LiveSession : IDisposable
    {
        private readonly MstscClientTracker _tracker;
        private readonly HashSet<int> _baseline;
        private readonly string _rdpPath;
        private readonly LoopbackSessionBridge _bridge;

        private int _disposed;

        public LiveSession(
            MstscClientTracker tracker,
            HashSet<int> baseline,
            string rdpPath,
            LoopbackSessionBridge bridge)
        {
            _tracker = tracker;
            _baseline = baseline;
            _rdpPath = rdpPath;
            _bridge = bridge;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                _tracker.CloseClients(_baseline, CloseGrace);
            }
            catch (Exception ex)
            {
                // Best effort: a client that cannot be reached is left to the
                // kill-on-close job / the guest-side session reclaim.
                Debug.WriteLine(ex);
            }

            try
            {
                _bridge.Dispose();
            }
            catch
            {
                // Best effort.
            }

            try
            {
                if (File.Exists(_rdpPath))
                {
                    File.Delete(_rdpPath);
                }
            }
            catch (IOException)
            {
                // The client may still hold the file; the temp folder is temp.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }
    }
}
