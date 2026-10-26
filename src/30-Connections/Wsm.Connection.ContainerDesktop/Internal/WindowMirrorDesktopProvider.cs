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
    /// <summary>Magic of the mirror file: <c>'WSMM'</c>. See <see cref="ContainerDesktopMirror"/>.</summary>
    private const int FrameOffset = ContainerDesktopMirror.PixelOffset;

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
        if (string.IsNullOrWhiteSpace(request.ContainerRoot))
        {
            throw new SandboxBackendException(
                "The window-mirror provider needs the host-visible root of the container's filesystem.", 0);
        }

        var directory = ContainerDesktopPaths.ToHostPath(
            request.ContainerRoot, ContainerDesktopPaths.ContainerAgentDirectory);
        var mirrorPath = Path.Combine(directory, Options.MirrorFileName);
        MirrorReadinessResult readiness;
        if (_sessions.TryGetValue(request.SandboxId.Value, out var existing))
        {
            readiness = await MirrorReadiness.ProbeAsync(existing.FramePath, cancellationToken).ConfigureAwait(false);
            if (readiness.State == MirrorReadinessState.Live)
            {
                return existing;
            }

            _sessions.TryRemove(request.SandboxId.Value, out _);
        }
        else
        {
            readiness = await MirrorReadiness.ProbeAsync(mirrorPath, cancellationToken).ConfigureAwait(false);
        }

        ContainerDesktopHeader header;
        if (readiness.State == MirrorReadinessState.Live)
        {
            header = readiness.Header!.Value;
        }
        else
        {
            EnsureRestartIsSafe(readiness.State);
            await LaunchReadSideAsync(request, directory, cancellationToken).ConfigureAwait(false);
            header = await WaitForMirrorAsync(mirrorPath, cancellationToken).ConfigureAwait(false);
        }

        var windows = ReadWindows(mirrorPath);
        var session = new ContainerDesktopSession(
            request.SandboxId, request.ContainerRoot, directory, mirrorPath, header.Geometry,
            DateTimeOffset.UtcNow, null, ContainerDesktopProviderIds.WindowMirror, windows);

        _sessions[request.SandboxId.Value] = session;
        _logger.LogInformation(
            "Window mirror for {SandboxId} is live: {Count} window(s), desktop {Width}x{Height}.",
            request.SandboxId, windows.Count, session.Geometry.Width, session.Geometry.Height);
        return session;
    }

    /// <inheritdoc />
    public async Task<ContainerDesktopSession?> AttachAsync(
        ContainerDesktopRequest request, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        if (string.IsNullOrWhiteSpace(request.ContainerRoot))
        {
            return null;
        }

        var directory = ContainerDesktopPaths.ToHostPath(
            request.ContainerRoot, ContainerDesktopPaths.ContainerAgentDirectory);
        var mirrorPath = Path.Combine(directory, Options.MirrorFileName);
        MirrorReadinessResult readiness;
        if (Get(request.SandboxId) is { } existing)
        {
            readiness = await MirrorReadiness.ProbeAsync(existing.FramePath, cancellationToken).ConfigureAwait(false);
            if (readiness.State == MirrorReadinessState.Live)
            {
                return existing;
            }

            _sessions.TryRemove(request.SandboxId.Value, out _);
        }
        else
        {
            readiness = await MirrorReadiness.ProbeAsync(mirrorPath, cancellationToken).ConfigureAwait(false);
        }

        ThrowIfCaptureFailed(readiness.State);
        if (readiness.State != MirrorReadinessState.Live)
        {
            return null;
        }

        var header = readiness.Header!.Value;
        var windows = ReadWindows(mirrorPath);
        var session = new ContainerDesktopSession(
            request.SandboxId, request.ContainerRoot, directory, mirrorPath, header.Geometry,
            DateTimeOffset.UtcNow, null, ContainerDesktopProviderIds.WindowMirror, windows);
        _sessions[request.SandboxId.Value] = session;
        _logger.LogInformation("Adopted the running window mirror for {SandboxId}.", request.SandboxId);
        return session;
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

    private async Task<ContainerDesktopHeader> WaitForMirrorAsync(
        string mirrorPath, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Options.FrameTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readiness = await MirrorReadiness.ProbeAsync(mirrorPath, cancellationToken).ConfigureAwait(false);
            if (readiness.State == MirrorReadinessState.Live)
            {
                return readiness.Header!.Value;
            }

            EnsureRestartIsSafe(readiness.State);
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new SandboxBackendException(
            "The container's window-mirror read side did not publish an advancing, pixel-producing capture. " +
            "The base image must be prepared first (wsm image mirror <layerPath>) and the container restarted.", 0);
    }

    private static void EnsureRestartIsSafe(MirrorReadinessState state)
    {
        ThrowIfCaptureFailed(state);
        if (state == MirrorReadinessState.UnknownHealth)
        {
            throw new SandboxBackendException(
                "The window-mirror capture health is unknown. The header will not be adopted, and a second " +
                "writer will not be started while the existing writer may still be running.", 0);
        }
    }

    private static void ThrowIfCaptureFailed(MirrorReadinessState state)
    {
        if (state == MirrorReadinessState.NoPixels)
        {
            throw new SandboxBackendException(
                "The window-mirror writer reports no captured pixels. A window table without pixels is not " +
                "a live desktop.", 0);
        }

        if (state == MirrorReadinessState.Error)
        {
            throw new SandboxBackendException("The window-mirror writer reports a capture error.", 0);
        }
    }

    /// <summary>
    /// Materialises the read side and starts it inside the container through HCS,
    /// detached, so the launch command returns immediately.
    /// </summary>
    private async Task LaunchReadSideAsync(
        ContainerDesktopRequest request, string directory, CancellationToken cancellationToken)
    {
        var engine = _registry.Find(SandboxEngineKind.WindowsContainer)
            ?? throw new SandboxBackendException("The Windows container engine is not registered.", 0);

        Directory.CreateDirectory(directory);
        InstallPayload(directory, "wsm-desktop-read.ps1");

        foreach (var stale in new[]
        {
            Options.MirrorFileName, Options.MirrorFileName + MirrorCaptureStatus.FileSuffix,
            Options.StopFileName, ContainerDesktopPaths.ModeFileName, "read.out", "read.log",
        })
        {
            TryDelete(Path.Combine(directory, stale));
        }

        // Mode negotiation lands before the read side starts. The writer also watches
        // this file so a viewer can request file mode after a passthrough failure.
        var mode = ShmModeNegotiation.Negotiate(
            ShmModeNegotiation.IsCurrentProcessElevated(), _launcher.CanLaunchIntoInteractiveSession);
        ShmModeNegotiation.WriteModeFile(directory, mode);

        // The read side detaches itself (see its -Detached switch), so HCS does not have to
        // wait for it - and no cmd/`start` sits in between, which would keep the launcher
        // alive for the child's lifetime and park HCS process creation for its timeout.
        var container = ContainerDesktopPaths.ContainerAgentDirectory;
        var command = string.Join(
            ' ',
            "powershell.exe -NoProfile -ExecutionPolicy Bypass -File",
            ContainerDesktopPaths.ReadAgent,
            "-AgentDirectory", container,
            "-MirrorFile", Options.MirrorFileName,
            "-InputPrefix", Options.InputFilePrefix,
            "-StopFile", Options.StopFileName,
            "-Width", Options.Width.ToString(CultureInfo.InvariantCulture),
            "-Height", Options.Height.ToString(CultureInfo.InvariantCulture),
            "-Fps", Options.Fps.ToString(CultureInfo.InvariantCulture),
            "-Seconds", Options.AgentLifetimeSeconds.ToString(CultureInfo.InvariantCulture));

        var exitCode = await engine
            .ExecuteAsync(request.SandboxId, new LogonCommand(command), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Window mirror read side launched for {SandboxId} through HCS (exit={ExitCode}).",
            request.SandboxId, exitCode);
    }

    private static void InstallPayload(string directory, string fileName)
    {
        var resource = $"Wsm.Connection.ContainerDesktop.Agent.{fileName}";
        using var stream = typeof(WindowMirrorDesktopProvider).Assembly.GetManifestResourceStream(resource)
            ?? throw new SandboxBackendException($"The embedded read side '{resource}' is missing.", 0);
        using var target = File.Create(Path.Combine(directory, fileName));
        stream.CopyTo(target);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Reads a non-empty window table through the shared mirror layout.</summary>
    private static IReadOnlyList<ContainerDesktopWindow> ReadWindows(string mirrorPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (true)
        {
            try
            {
                if (ContainerDesktopMirror.TryReadHeaderInfo(mirrorPath) is { } fileHeader)
                {
                    IReadOnlyList<ContainerDesktopWindow> windows;
                    if (fileHeader.Mode != ContainerDesktopMode.Pagefile)
                    {
                        windows = ContainerDesktopMirror.ReadWindows(
                            mirrorPath, fileHeader.Geometry, fileHeader.WindowCount, fileHeader.Version);
                    }
                    else
                    {
                        using var source = SharedSectionSource.Open(mirrorPath, ContainerDesktopMirror.Magic,
                            mapped => ContainerDesktopMirror.TryReadHeaderInfo(
                                    mapped, ContainerDesktopFrame.HeaderFileCapacity) is { } parsed
                                ? ContainerDesktopMirror.Capacity(parsed.Geometry, parsed.Version)
                                : 0);
                        var header = ContainerDesktopMirror.TryReadHeaderInfo(source.BaseAddress, source.Length)
                            ?? fileHeader;
                        windows = ContainerDesktopMirror.ReadWindows(
                            source.BaseAddress, source.Length, header.Geometry, header.WindowCount, header.Version);
                    }

                    if (windows.Count > 0)
                    {
                        return windows;
                    }

                    if (fileHeader.WindowCount == 0)
                    {
                        break;
                    }
                }
            }
            catch (IOException)
            {
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }
            Thread.Sleep(25);
        }

        throw new SandboxBackendException(
            "The window-mirror writer published no readable windows. An empty window table is not " +
            "a usable live desktop, even when its health token or heartbeat looks valid.", 0);
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
