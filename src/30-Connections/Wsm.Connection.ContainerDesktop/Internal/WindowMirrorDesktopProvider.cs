using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;

namespace Wsm.Connection.ContainerDesktop.Internal;

/// <summary>
/// <b>Window mirror</b> container desktop provider (<c>window-mirror</c>) - the
/// doc's section 18 design:
/// <list type="number">
///   <item>the container's <c>winlogon</c> is wrapped (an <b>image-level</b> change,
///   see <c>IContainerImageCustomizer.EnableWindowMirror</c>), so our read side
///   starts <i>inside the container's own interactive session</i> - no HCS process
///   creation, no cross-silo injection, neither of which is possible (measured);</item>
///   <item>that read side publishes one shared mirror file: the desktop frame
///   <b>plus a table of top-level windows</b> (rect, title, sequence) and only writes
///   the regions that changed;</item>
///   <item>the host shows one <b>real host window per container window</b>, so the
///   user's own DWM composes them and they behave like native windows.</item>
/// </list>
/// This provider only <b>adopts</b> (or waits for) that read side: it cannot start it,
/// because it is started by the container's own logon.
/// </summary>
internal sealed class WindowMirrorDesktopProvider
    : IContainerDesktopProvider, IContainerDesktopRootStop, ISingletonDependency
{
    /// <summary>Magic of the mirror file: <c>'WSMM'</c>.</summary>
    private const int MirrorMagic = 0x4D4D5357;

    /// <summary>Frame header size; the pixel buffer starts here.</summary>
    private const int PixelOffset = 64;

    /// <summary>Bytes per window table entry: id(8) x(4) y(4) w(4) h(4) seq(8) titleOffset(4) titleLength(4).</summary>
    private const int WindowEntrySize = 40;

    /// <summary>Bytes reserved for the window table (before the title blob).</summary>
    private const int WindowTableBytes = 8192;

    /// <summary>Bytes reserved for window titles, after the table.</summary>
    private const int TitleBlobBytes = 16384;

    private readonly ConcurrentDictionary<Guid, ContainerDesktopSession> _sessions = new();
    private readonly ISandboxEngineRegistry _registry;
    private readonly ISessionProcessLauncher _launcher;
    private readonly IOptions<ContainerDesktopOptions> _options;
    private readonly ILogger<WindowMirrorDesktopProvider> _logger;

    public WindowMirrorDesktopProvider(
        ISandboxEngineRegistry registry,
        ISessionProcessLauncher launcher,
        IOptions<ContainerDesktopOptions> options,
        ILogger<WindowMirrorDesktopProvider> logger)
    {
        _registry = registry;
        _launcher = launcher;
        _options = options;
        _logger = logger;
    }

    private ContainerDesktopOptions Options => _options.Value;

    /// <inheritdoc />
    public string Id => ContainerDesktopProviderIds.WindowMirror;

    /// <inheritdoc />
    public ContainerDesktopKind Kind => ContainerDesktopKind.WindowMirror;

    /// <inheritdoc />
    public bool IsSupported => Options.ShouldEnable;

    /// <inheritdoc />
    public IReadOnlyList<ContainerDesktopSession> Sessions => _sessions.Values.ToArray();

    /// <inheritdoc />
    public ContainerDesktopSession? Get(SandboxInstanceId id)
        => _sessions.TryGetValue(id.Value, out var session) ? session : null;

    /// <inheritdoc />
    public async Task<ContainerDesktopSession> StartAsync(
        ContainerDesktopRequest request, CancellationToken cancellationToken = default)
    {
        EnsureSupported();

        if (_sessions.TryGetValue(request.SandboxId.Value, out var live))
        {
            return live;
        }

        if (string.IsNullOrWhiteSpace(request.ContainerRoot))
        {
            throw new SandboxBackendException(
                "The window-mirror provider needs the host-visible root of the container's filesystem.", 0);
        }

        var directory = ContainerDesktopPaths.ToHostPath(
            request.ContainerRoot, ContainerDesktopPaths.ContainerAgentDirectory);
        var mirrorPath = Path.Combine(directory, Options.MirrorFileName);

        var geometry = TryReadGeometry(mirrorPath)
            ?? await WaitForMirrorAsync(mirrorPath, cancellationToken).ConfigureAwait(false);
        var windows = TryReadWindows(mirrorPath, geometry) ?? Array.Empty<ContainerDesktopWindow>();

        var session = new ContainerDesktopSession(
            request.SandboxId, request.ContainerRoot, directory, mirrorPath, geometry,
            DateTimeOffset.UtcNow, null, ContainerDesktopProviderIds.WindowMirror, windows);

        _sessions[request.SandboxId.Value] = session;
        _logger.LogInformation(
            "Window mirror for {SandboxId} is live: {Count} window(s), desktop {Width}x{Height}.",
            request.SandboxId, windows.Count, geometry.Width, geometry.Height);
        return session;
    }

    /// <inheritdoc />
    public Task<ContainerDesktopSession?> AttachAsync(
        ContainerDesktopRequest request, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        if (string.IsNullOrWhiteSpace(request.ContainerRoot))
        {
            return Task.FromResult<ContainerDesktopSession?>(null);
        }

        if (Get(request.SandboxId) is { } live)
        {
            return Task.FromResult<ContainerDesktopSession?>(live);
        }

        var directory = ContainerDesktopPaths.ToHostPath(
            request.ContainerRoot, ContainerDesktopPaths.ContainerAgentDirectory);
        var mirrorPath = Path.Combine(directory, Options.MirrorFileName);
        if (TryReadGeometry(mirrorPath) is not { } geometry)
        {
            return Task.FromResult<ContainerDesktopSession?>(null);
        }

        var windows = TryReadWindows(mirrorPath, geometry) ?? Array.Empty<ContainerDesktopWindow>();
        var session = new ContainerDesktopSession(
            request.SandboxId, request.ContainerRoot, directory, mirrorPath, geometry,
            DateTimeOffset.UtcNow, null, ContainerDesktopProviderIds.WindowMirror, windows);
        _sessions[request.SandboxId.Value] = session;
        _logger.LogInformation("Adopted the running window mirror for {SandboxId}.", request.SandboxId);
        return Task.FromResult<ContainerDesktopSession?>(session);
    }

    /// <inheritdoc />
    public Task<ContainerDesktopSession> PresentAsync(
        SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var session = Require(id);
        var viewer = ResolveViewerCommand();
        var arguments = string.Format(CultureInfo.InvariantCulture, Options.MirrorViewerArguments, session.FramePath);
        if (Options.ViewerLifetimeSeconds > 0)
        {
            arguments += string.Create(CultureInfo.InvariantCulture, $" --seconds {Options.ViewerLifetimeSeconds}");
        }

        var result = _launcher.Launch($"\"{viewer}\" {arguments}", session.AgentDirectory);
        if (!result.Launched)
        {
            throw new SandboxBackendException($"Could not start the window mirror viewer: {result.Detail}", 0);
        }

        _logger.LogInformation(
            "Mirrored container desktop {SandboxId} as pid {Pid} in session {SessionId} ({Detail}).",
            id, result.ProcessId, result.SessionId, result.Detail);

        var updated = session with { ViewerProcessId = result.ProcessId };
        _sessions[id.Value] = updated;
        return Task.FromResult(updated);
    }

    /// <inheritdoc />
    public Task SendInputAsync(SandboxInstanceId id, string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new SandboxBackendException("An input command must not be empty.", 0);
        }

        var session = Require(id);

        // Same one-file-per-command protocol as the frame provider: the container keeps
        // a handle on a file once it has read it, so the queue is a directory listing.
        var name = Options.InputFilePrefix
            + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)
            + ".txt";
        File.WriteAllText(Path.Combine(session.InputDirectory, name), command, new UTF8Encoding(false));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryRemove(id.Value, out var session))
        {
            return Task.CompletedTask;
        }

        return WriteStopMarker(session.AgentDirectory, id);
    }

    /// <inheritdoc />
    public Task StopAsync(string computeSystemId, string containerRoot, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(computeSystemId, out var value))
        {
            return Task.CompletedTask;
        }

        _sessions.TryRemove(value, out _);
        return WriteStopMarker(AgentDirectoryFor(containerRoot), SandboxInstanceId.From(value));
    }

    private Task WriteStopMarker(string directory, SandboxInstanceId id)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, Options.StopFileName), "stop");
            _logger.LogInformation("Asked the window mirror read side for {SandboxId} to exit.", id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signalling the window mirror read side for {SandboxId} failed.", id);
        }

        return Task.CompletedTask;
    }

    private async Task<ContainerDesktopGeometry> WaitForMirrorAsync(
        string mirrorPath, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Options.FrameTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadGeometry(mirrorPath) is { } geometry)
            {
                return geometry;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new SandboxBackendException(
            "The container's window-mirror read side is not publishing. It is started by the container's own " +
            "logon, so the base image must be prepared first (wsm image mirror <layerPath>) and the container " +
            "restarted afterwards - a container that booted before the patch cannot have it.", 0);
    }

    /// <summary>Reads the shared header (same layout as the frame buffer, different magic).</summary>
    private static ContainerDesktopGeometry? TryReadGeometry(string mirrorPath)
    {
        try
        {
            if (!File.Exists(mirrorPath))
            {
                return null;
            }

            using var stream = new FileStream(mirrorPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[PixelOffset];
            if (stream.Read(header) < PixelOffset)
            {
                return null;
            }

            return BitConverter.ToInt32(header[..4]) == MirrorMagic && BitConverter.ToInt32(header.Slice(8, 4)) > 0
                ? new ContainerDesktopGeometry(
                    BitConverter.ToInt32(header.Slice(8, 4)),
                    BitConverter.ToInt32(header.Slice(12, 4)),
                    BitConverter.ToInt32(header.Slice(16, 4)),
                    BitConverter.ToInt32(header.Slice(20, 4)))
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Reads the published window table, if the read side has written one.</summary>
    private static IReadOnlyList<ContainerDesktopWindow>? TryReadWindows(
        string mirrorPath, ContainerDesktopGeometry geometry)
    {
        try
        {
            using var stream = new FileStream(mirrorPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[48];
            if (stream.Read(header) < 48)
            {
                return null;
            }

            var count = BitConverter.ToInt32(header.Slice(40, 4));
            if (count <= 0)
            {
                return Array.Empty<ContainerDesktopWindow>();
            }

            count = Math.Min(count, WindowTableBytes / WindowEntrySize);

            var tableStart = (long)PixelOffset + (long)geometry.Stride * geometry.Height;
            var titles = tableStart + WindowTableBytes;

            var entry = new byte[WindowEntrySize];
            var windows = new List<ContainerDesktopWindow>(count);
            for (var i = 0; i < count; i++)
            {
                stream.Position = tableStart + (long)i * WindowEntrySize;
                if (stream.Read(entry) < WindowEntrySize)
                {
                    break;
                }

                var id = BitConverter.ToInt64(entry.AsSpan(0, 8));
                var titleOffset = BitConverter.ToInt32(entry.AsSpan(32, 4));
                var titleLength = BitConverter.ToInt32(entry.AsSpan(36, 4));
                var title = string.Empty;
                if (titleLength is > 0 and <= 512)
                {
                    var buffer = new byte[titleLength * 2];
                    stream.Position = titles + titleOffset;
                    if (stream.Read(buffer) == buffer.Length)
                    {
                        title = Encoding.Unicode.GetString(buffer);
                    }
                }

                windows.Add(new ContainerDesktopWindow(
                    id,
                    title,
                    BitConverter.ToInt32(entry.AsSpan(8, 4)),
                    BitConverter.ToInt32(entry.AsSpan(12, 4)),
                    BitConverter.ToInt32(entry.AsSpan(16, 4)),
                    BitConverter.ToInt32(entry.AsSpan(20, 4)),
                    BitConverter.ToInt64(entry.AsSpan(24, 8))));
            }

            return windows;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private ContainerDesktopSession Require(SandboxInstanceId id)
        => Get(id) ?? throw new SandboxBackendException(
            $"No window mirror is running for {id}. Prepare the image (wsm image mirror <layerPath>), " +
            "restart the container and try again.", 0);

    private void EnsureSupported()
    {
        if (!Options.ShouldEnable)
        {
            throw new SandboxBackendException(
                "The container desktop channel is disabled (" + ContainerDesktopOptions.EnableVariable + "=0).", 0);
        }
    }

    private string ResolveViewerCommand()
    {
        if (!string.IsNullOrWhiteSpace(Options.ViewerCommand))
        {
            return Options.ViewerCommand!;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "wsm.exe");
        return File.Exists(beside) ? beside : Environment.ProcessPath ?? "wsm.exe";
    }

    private static string AgentDirectoryFor(string containerRoot)
        => ContainerDesktopPaths.ToHostPath(containerRoot, ContainerDesktopPaths.ContainerAgentDirectory);
}
