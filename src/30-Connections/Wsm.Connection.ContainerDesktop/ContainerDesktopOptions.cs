namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// Tuning for the container desktop channel. The defaults match the measured
/// behaviour of the in-container agent (see
/// <c>docs/investigation/13-container-desktop-and-rdp.md</c>): a 1024x768
/// desktop captured a few times per second into one memory-mapped file.
/// </summary>
public sealed class ContainerDesktopOptions
{
    /// <summary>Configuration section bound by the host.</summary>
    public const string SectionName = "ContainerDesktop";

    /// <summary>
    /// Environment variable that switches the whole capability on or off
    /// (like the container engine's own feature switches).
    /// </summary>
    public const string EnableVariable = "WSM_CONTAINER_DESKTOP_VIEW";

    /// <summary>Whether the desktop channel may be started at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Directory <b>inside the container</b> that holds the agent and its files.</summary>
    public string AgentDirectory { get; set; } = @"C:\wsm";

    /// <summary>File name of the capture/input agent written into the container.</summary>
    public string AgentFileName { get; set; } = "container-desktop-agent.ps1";

    /// <summary>Name of the launcher script that starts the agent detached.</summary>
    public string LauncherFileName { get; set; } = "agent-start.cmd";

    /// <summary>Name of the shared-memory frame file inside the agent directory.</summary>
    public string FrameFileName { get; set; } = "frame.shm";

    /// <summary>
    /// Name of the shared-memory mirror file the window-mirror read side publishes
    /// (a full desktop frame plus a top-level window table).
    /// </summary>
    public string MirrorFileName { get; set; } = "mirror.shm";

    /// <summary>Prefix of the one-file-per-command input queue.</summary>
    public string InputFilePrefix { get; set; } = "input-";

    /// <summary>Marker file that makes a running agent exit.</summary>
    public string StopFileName { get; set; } = "agent-stop.txt";

    /// <summary>Captured desktop width in pixels.</summary>
    public int Width { get; set; } = 1024;

    /// <summary>Captured desktop height in pixels.</summary>
    public int Height { get; set; } = 768;

    /// <summary>Frames per second the agent targets.</summary>
    public int Fps { get; set; } = 5;

    /// <summary>How long a started agent keeps running before it exits by itself.</summary>
    public int AgentLifetimeSeconds { get; set; } = 900;

    /// <summary>How long to wait for the agent's first frame.</summary>
    public int FrameTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// The viewer executable used to present a session. When unset the host looks
    /// for <c>wsm.exe</c> next to itself and finally falls back to its own
    /// executable (the CLI implements the <c>desktop-view</c> command).
    /// </summary>
    public string? ViewerCommand { get; set; }

    /// <summary>Arguments template for the viewer; {0} is the frame file path.</summary>
    public string ViewerArguments { get; set; } = "desktop-view --frame \"{0}\"";

    /// <summary>
    /// Viewer arguments for the window-mirror provider: the viewer creates one host
    /// window per container window instead of one window for the whole desktop.
    /// </summary>
    public string MirrorViewerArguments { get; set; } = "desktop-view --mode windows --frame \"{0}\"";

    /// <summary>How long the presented viewer window stays open (0 = until closed).</summary>
    public int ViewerLifetimeSeconds { get; set; }

    internal bool ShouldEnable => Enabled
        && !string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "0", StringComparison.Ordinal);
}
