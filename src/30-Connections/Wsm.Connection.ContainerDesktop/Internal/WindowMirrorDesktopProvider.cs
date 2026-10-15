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

        // Adopt a publishing read side; otherwise start one through HCS, which is the
        // supported way to run a process inside the container's session. (The image's
        // logon hook is the alternative when the GUI lives in the interactive session -
        // see IContainerImageCustomizer.EnableWindowMirror.)
        var geometry = TryReadGeometry(mirrorPath);
        if (geometry is null)
        {
            await LaunchReadSideAsync(request, directory, cancellationToken).ConfigureAwait(false);
            geometry = await WaitForMirrorAsync(mirrorPath, cancellationToken).ConfigureAwait(false);
        }

        var windows = ReadWindows(mirrorPath);

        var session = new ContainerDesktopSession(
            request.SandboxId, request.ContainerRoot, directory, mirrorPath, geometry.Value,
            DateTimeOffset.UtcNow, null, ContainerDesktopProviderIds.WindowMirror, windows);

        _sessions[request.SandboxId.Value] = session;
        _logger.LogInformation(
            "Window mirror for {SandboxId} is live: {Count} window(s), desktop {Width}x{Height}.",
            request.SandboxId, windows.Count, session.Geometry.Width, session.Geometry.Height);
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

        var windows = ReadWindows(mirrorPath);
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
        InstallPayload(directory, "wsm-desktop-read.cmd");

        foreach (var stale in new[] { Options.MirrorFileName, Options.StopFileName, "read.out", "read.log" })
        {
            TryDelete(Path.Combine(directory, stale));
        }

        // A starter whose quotes live in a plain .cmd, so the launch command line stays
        // free of nested quotes; `start` detaches, so HCS does not wait for the read side.
        const string starterName = "wsm-mirror-start.cmd";
        var containerDirectory = ContainerDesktopPaths.ContainerAgentDirectory;
        var starter = string.Join(
            "\r\n",
            "@echo off",
            $"start \"\" /d \"{containerDirectory}\" cmd.exe /c wsm-desktop-read.cmd",
            string.Empty);
        File.WriteAllText(Path.Combine(directory, starterName), starter, new UTF8Encoding(false));

        var launcher = Path.Combine(containerDirectory, starterName);
        var exitCode = await engine
            .ExecuteAsync(request.SandboxId, new LogonCommand($"cmd.exe /c {launcher}"), cancellationToken)
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

    /// <summary>Reads the shared header through the shared mirror layout.</summary>
    private static ContainerDesktopGeometry? TryReadGeometry(string mirrorPath)
        => ContainerDesktopMirror.TryReadHeader(mirrorPath, out _, out _);

    /// <summary>Reads the published window table through the shared mirror layout.</summary>
    private static IReadOnlyList<ContainerDesktopWindow> ReadWindows(string mirrorPath)
        => ContainerDesktopMirror.TryReadHeader(mirrorPath, out var count, out _) is { } geometry
            ? ContainerDesktopMirror.ReadWindows(mirrorPath, geometry, count)
            : Array.Empty<ContainerDesktopWindow>();

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
