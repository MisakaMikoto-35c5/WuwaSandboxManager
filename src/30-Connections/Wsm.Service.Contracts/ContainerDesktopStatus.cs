using Wsm.Domain.ValueObjects;

namespace Wsm.Service.Contracts;

/// <summary>
/// Client-side view of a container desktop channel, as reported by the WSM agent's
/// <c>StartDesktop</c>/<c>GetDesktopStatus</c> RPCs. Carries everything a caller
/// needs to present the desktop itself: the shared frame file path (the viewer
/// argument) plus the provider/kind that decides which viewer mode to use.
/// </summary>
/// <param name="Running">Whether a live channel exists for the sandbox.</param>
/// <param name="ProviderId">The provider producing the channel (e.g. <c>frame-buffer</c>).</param>
/// <param name="Kind">The channel's kind; <see cref="ContainerDesktopKind.None"/> when not running/unknown.</param>
/// <param name="FramePath">Host path of the shared frame file (empty when not running).</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Fps">Frame rate the agent targets.</param>
/// <param name="WindowCount">
/// Top-level windows the provider mirrors, or <c>null</c> when one frame is the
/// whole desktop (the frame-buffer provider).
/// </param>
/// <param name="ViewerProcessId">Process id of the presented viewer, when one was started.</param>
public sealed record ContainerDesktopStatus(
    bool Running,
    string ProviderId,
    ContainerDesktopKind Kind,
    string FramePath,
    int Width,
    int Height,
    int Fps,
    int? WindowCount,
    int? ViewerProcessId)
{
    /// <summary>The status of a sandbox with no live desktop channel.</summary>
    public static ContainerDesktopStatus NotRunning { get; } = new(
        Running: false,
        ProviderId: string.Empty,
        Kind: ContainerDesktopKind.None,
        FramePath: string.Empty,
        Width: 0,
        Height: 0,
        Fps: 0,
        WindowCount: null,
        ViewerProcessId: null);
}
