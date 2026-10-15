using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.ContainerDesktop;

/// <summary>Geometry and cadence of a container desktop frame buffer.</summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Stride">Bytes per row (always <c>Width * 4</c> today).</param>
/// <param name="Fps">Frame rate the agent targets.</param>
public readonly record struct ContainerDesktopGeometry(int Width, int Height, int Stride, int Fps);

/// <summary>
/// A live container desktop channel: the agent inside the container, the shared
/// frame buffer it writes (<c>frame.shm</c>), and the input queue the host drops
/// commands into.
/// <para>
/// Everything is addressed through the container's <b>host-visible root</b>
/// (<see cref="ContainerRoot"/>) - for a process-isolated container that is the
/// mounted scratch volume, so a plain file there is shared memory: both sides map
/// the same file object and the same cached pages.
/// </para>
/// </summary>
/// <param name="SandboxId">The sandbox this desktop belongs to.</param>
/// <param name="ContainerRoot">Host path that is the container's <c>C:\</c>.</param>
/// <param name="AgentDirectory">Host path of the agent directory (container: <c>C:\wsm</c>).</param>
/// <param name="FramePath">Host path of the shared frame file.</param>
/// <param name="Geometry">Frame geometry.</param>
/// <param name="StartedAt">When the channel was started.</param>
/// <param name="ViewerProcessId">Process id of the presented viewer, when one was started.</param>
public sealed record ContainerDesktopSession(
    SandboxInstanceId SandboxId,
    string ContainerRoot,
    string AgentDirectory,
    string FramePath,
    ContainerDesktopGeometry Geometry,
    DateTimeOffset StartedAt,
    int? ViewerProcessId = null)
{
    /// <summary>Host path of the input queue directory.</summary>
    public string InputDirectory => AgentDirectory;

    /// <summary>Whether the frame file currently exists.</summary>
    public bool HasFrames => File.Exists(FramePath);
}
