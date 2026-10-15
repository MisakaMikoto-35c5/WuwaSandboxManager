using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;

namespace Wsm.Connection.ContainerDesktop.Internal;

/// <summary>
/// <b>Frame buffer</b> container desktop provider (<c>frame-buffer</c>): it
/// materialises an agent into the container through its host-visible root, starts it
/// detached inside the container, waits for its first frame and can present it as a
/// window in the interactive session. One frame = the whole desktop, so it copies
/// whole frames; it needs no image change. See doc sections 13-14.
/// </summary>
internal sealed class FrameBufferDesktopProvider : IContainerDesktopProvider, IContainerDesktopRootStop, ISingletonDependency
{
    private const string AgentResourceName =
        "Wsm.Connection.ContainerDesktop.Agent.container-desktop-agent.ps1";

    private const int FrameMagic = 0x314D5346; // 'FSM1'

    private readonly ConcurrentDictionary<Guid, ContainerDesktopSession> _sessions = new();
    private readonly ISandboxEngineRegistry _registry;
    private readonly ISessionProcessLauncher _launcher;
    private readonly IOptions<ContainerDesktopOptions> _options;
    private readonly ILogger<FrameBufferDesktopProvider> _logger;

    public FrameBufferDesktopProvider(
        ISandboxEngineRegistry registry,
        ISessionProcessLauncher launcher,
        IOptions<ContainerDesktopOptions> options,
        ILogger<FrameBufferDesktopProvider> logger)
    {
        _registry = registry;
        _launcher = launcher;
        _options = options;
        _logger = logger;
    }

    private ContainerDesktopOptions Options => _options.Value;

    /// <inheritdoc />
    public string Id => ContainerDesktopProviderIds.FrameBuffer;

    /// <inheritdoc />
    public ContainerDesktopKind Kind => ContainerDesktopKind.FrameBuffer;

    /// <inheritdoc />
    public bool IsSupported => Options.ShouldEnable;

    /// <inheritdoc />
    public Task<ContainerDesktopSession> StartAsync(
        ContainerDesktopRequest request, CancellationToken cancellationToken = default)
        => string.IsNullOrWhiteSpace(request.ContainerRoot)
            ? StartAsync(request.SandboxId, cancellationToken)
            : AttachAsync(request.ComputeSystemId, request.ContainerRoot, cancellationToken);

    /// <inheritdoc />
    public Task<ContainerDesktopSession?> AttachAsync(
        ContainerDesktopRequest request, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        if (string.IsNullOrWhiteSpace(request.ContainerRoot))
        {
            return Task.FromResult<ContainerDesktopSession?>(null);
        }

        // Adopt only: a valid frame header means an agent is already publishing.
        var directory = AgentDirectoryFor(request.ContainerRoot);
        var framePath = Path.Combine(directory, Options.FrameFileName);
        if (TryReadFrameHeader(framePath) is not { } geometry)
        {
            return Task.FromResult<ContainerDesktopSession?>(null);
        }

        var session = new ContainerDesktopSession(
            request.SandboxId, request.ContainerRoot, directory, framePath, geometry,
            DateTimeOffset.UtcNow, null, ContainerDesktopProviderIds.FrameBuffer);
        _sessions[request.SandboxId.Value] = session;
        _logger.LogInformation("Adopted the running frame buffer desktop for {SandboxId}.", request.SandboxId);
        return Task.FromResult<ContainerDesktopSession?>(session);
    }

    /// <inheritdoc />
    public IReadOnlyList<ContainerDesktopSession> Sessions => _sessions.Values.ToArray();

    /// <inheritdoc />
    public ContainerDesktopSession? Get(SandboxInstanceId id)
        => _sessions.TryGetValue(id.Value, out var session) ? session : null;

    /// <inheritdoc />
    public async Task<ContainerDesktopSession> StartAsync(
        SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        if (_sessions.TryGetValue(id.Value, out var live))
        {
            return live;
        }

        var engine = RequireEngine();
        if (engine is not IContainerFileSystemAccess fileSystem)
        {
            throw new SandboxBackendException(
                "The active container engine cannot expose the container filesystem, so the desktop " +
                "channel cannot materialise its agent.", 0);
        }

        var root = fileSystem.GetContainerRoot(id);
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new SandboxBackendException($"Container {id} is not running.", 0);
        }

        return await AttachAsync(id.ToString(), root!, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContainerDesktopSession> AttachAsync(
        string computeSystemId, string containerRoot, CancellationToken cancellationToken = default)
    {
        EnsureSupported();
        if (!Guid.TryParse(computeSystemId, out var value))
        {
            throw new SandboxBackendException($"'{computeSystemId}' is not a compute-system/sandbox id.", 0);
        }

        var id = SandboxInstanceId.From(value);
        if (_sessions.TryGetValue(value, out var live))
        {
            return live;
        }

        var directory = AgentDirectoryFor(containerRoot);
        var framePath = Path.Combine(directory, Options.FrameFileName);

        // Adopt a live agent rather than restarting one: a second caller (the
        // service, or another CLI invocation) must not fight the running agent for
        // the frame file. A valid header is the liveness signal.
        if (TryReadFrameHeader(framePath) is { } adopted)
        {
            var existing = new ContainerDesktopSession(
                id, containerRoot, directory, framePath, adopted, DateTimeOffset.UtcNow);
            _sessions[value] = existing;
            _logger.LogInformation("Adopted the running container desktop agent for {SandboxId}.", id);
            return existing;
        }

        Directory.CreateDirectory(directory);
        ResetAgentDirectory(directory);

        var engine = RequireEngine();
        var launcherPath = Path.Combine(Options.AgentDirectory, Options.LauncherFileName); // inside the container
        var exitCode = await engine
            .ExecuteAsync(id, new LogonCommand($"cmd.exe /c {launcherPath}"), cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Container desktop agent launch for {SandboxId} was dispatched (exit={ExitCode}).", id, exitCode);

        var geometry = await WaitForFirstFrameAsync(framePath, directory, cancellationToken).ConfigureAwait(false);

        var session = new ContainerDesktopSession(
            id, containerRoot, directory, framePath, geometry, DateTimeOffset.UtcNow);
        _sessions[value] = session;
        _logger.LogInformation(
            "Container desktop for {SandboxId} is live: {Width}x{Height} @ {Fps}fps, frames in {FramePath}.",
            id, geometry.Width, geometry.Height, geometry.Fps, framePath);
        return session;
    }

    /// <inheritdoc />
    public Task<ContainerDesktopSession> PresentAsync(
        SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var session = RequireSession(id);
        var viewer = ResolveViewerCommand();
        var arguments = string.Format(CultureInfo.InvariantCulture, Options.ViewerArguments, session.FramePath);
        if (Options.ViewerLifetimeSeconds > 0)
        {
            arguments += string.Create(
                CultureInfo.InvariantCulture, $" --seconds {Options.ViewerLifetimeSeconds}");
        }

        var result = _launcher.Launch($"\"{viewer}\" {arguments}", session.AgentDirectory);
        if (!result.Launched)
        {
            throw new SandboxBackendException($"Could not start the container desktop viewer: {result.Detail}", 0);
        }

        _logger.LogInformation(
            "Presented container desktop {SandboxId} as pid {Pid} in session {SessionId} ({Detail}).",
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

        var session = RequireSession(id);

        // One fresh file per command: the container's filesystem keeps a handle on
        // a layer file once it has read it, so rewriting a single queue file fails
        // with "used by another process" (measured).
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

        try
        {
            // The agent polls this marker and exits; tearing the frame file down
            // ourselves would race its mapping.
            File.WriteAllText(Path.Combine(session.AgentDirectory, Options.StopFileName), "stop");
            _logger.LogInformation("Asked the container desktop agent for {SandboxId} to exit.", id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signalling the container desktop agent for {SandboxId} failed.", id);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(string computeSystemId, string containerRoot, CancellationToken cancellationToken = default)
    {
        var directory = AgentDirectoryFor(containerRoot);
        try
        {
            Directory.CreateDirectory(directory);
            WriteAllText(Path.Combine(directory, Options.StopFileName), "stop");
            if (Guid.TryParse(computeSystemId, out var value))
            {
                _sessions.TryRemove(value, out _);
            }

            _logger.LogInformation("Asked the container desktop agent under {Root} to exit.", containerRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Signalling the container desktop agent under {Root} failed.", containerRoot);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Turns a container-side directory (<c>C:\wsm</c>) into the relative path to
    /// combine with the container root. It must not stay rooted: Path.Combine
    /// *replaces* the first segment when the second is rooted, which would point
    /// the host at its own <c>C:\wsm</c> instead of the container's.
    /// </summary>
    internal static string ToRelativeContainerPath(string containerDirectory)
        => ContainerDesktopPaths.ToRelative(containerDirectory);

    private string AgentDirectoryFor(string containerRoot)
        => Path.Combine(containerRoot.TrimEnd('\\'), ToRelativeContainerPath(Options.AgentDirectory));

    private void ResetAgentDirectory(string directory)    {
        WriteAllText(Path.Combine(directory, Options.AgentFileName), ReadEmbeddedAgent());

        var containerDirectory = Options.AgentDirectory.TrimEnd('\\');
        var scriptPath = $"{containerDirectory}\\{Options.AgentFileName}";
        var runPath = $"{containerDirectory}\\agent-run.cmd";
        var outputPath = $"{containerDirectory}\\agent.out";

        // agent-run.cmd performs the actual run (quotes are fine in a plain .cmd) ...
        var run = string.Join(
            "\r\n",
            "@echo off",
            "powershell.exe -NoProfile -ExecutionPolicy Bypass -File "
                + $"\"{scriptPath}\" -AgentDirectory \"{containerDirectory}\" "
                + $"-FrameFile \"{Options.FrameFileName}\" -InputPrefix \"{Options.InputFilePrefix}\" "
                + $"-StopFile \"{Options.StopFileName}\" -Width {Options.Width} -Height {Options.Height} "
                + $"-Fps {Options.Fps} -Seconds {Options.AgentLifetimeSeconds} "
                + $"> \"{outputPath}\" 2>&1",
            string.Empty);
        WriteAllText(Path.Combine(directory, "agent-run.cmd"), run);

        // ... and agent-start.cmd detaches it, so the command that starts the agent
        // returns immediately (HCS waits for process exit otherwise). No nested
        // quoting: `start` gets the working directory via /d and a bare file name.
        var start = string.Join(
            "\r\n",
            "@echo off",
            $"start \"\" /d \"{containerDirectory}\" cmd.exe /c agent-run.cmd",
            string.Empty);
        WriteAllText(Path.Combine(directory, Options.LauncherFileName), start);

        // Stale state from a previous agent must not be mistaken for the new one.
        foreach (var stale in new[] { Options.FrameFileName, Options.StopFileName, "agent.out", "agent.log" })
        {
            TryDelete(Path.Combine(directory, stale));
        }

        foreach (var input in Directory.EnumerateFiles(directory, Options.InputFilePrefix + "*.txt"))
        {
            TryDelete(input);
        }
    }

    private async Task<ContainerDesktopGeometry> WaitForFirstFrameAsync(
        string framePath, string directory, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Options.FrameTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var geometry = TryReadFrameHeader(framePath);
            if (geometry is { } read)
            {
                return read;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new SandboxBackendException(
            $"The container desktop agent did not produce a frame within {Options.FrameTimeoutSeconds}s "
            + $"({Path.Combine(directory, "agent.out")}).", 0);
    }

    private static ContainerDesktopGeometry? TryReadFrameHeader(string framePath)
    {
        try
        {
            if (!File.Exists(framePath))
            {
                return null;
            }

            using var stream = new FileStream(framePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) < header.Length)
            {
                return null;
            }

            var magic = BitConverter.ToInt32(header[..4]);
            var width = BitConverter.ToInt32(header.Slice(8, 4));
            var height = BitConverter.ToInt32(header.Slice(12, 4));
            var stride = BitConverter.ToInt32(header.Slice(16, 4));
            var fps = BitConverter.ToInt32(header.Slice(20, 4));
            return magic == FrameMagic && width > 0 && height > 0
                ? new ContainerDesktopGeometry(width, height, stride, fps)
                : null;
        }
        catch (IOException)
        {
            return null; // the agent is mid-write; try again
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

    private static string ReadEmbeddedAgent()
    {
        using var stream = typeof(ContainerDesktopHost).Assembly.GetManifestResourceStream(AgentResourceName)
            ?? throw new SandboxBackendException(
                $"The embedded container desktop agent ('{AgentResourceName}') is missing.", 0);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private ISandboxEngine RequireEngine()
        => _registry.Find(SandboxEngineKind.WindowsContainer)
           ?? throw new SandboxBackendException("The Windows container engine is not registered.", 0);

    private ContainerDesktopSession RequireSession(SandboxInstanceId id)
        => Get(id) ?? throw new SandboxBackendException(
            $"No container desktop is running for {id}; start it first.", 0);

    private void EnsureSupported()
    {
        if (!Options.ShouldEnable)
        {
            throw new SandboxBackendException(
                "The container desktop channel is disabled (" + ContainerDesktopOptions.EnableVariable + "=0).", 0);
        }
    }

    private static void WriteAllText(string path, string content)
        => File.WriteAllText(path, content, new UTF8Encoding(false));

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
}
