namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// One top-level window seen inside the container's own session. The read side
/// publishes these so the host can mirror each of them as a real window (rect, title,
/// z-order) instead of showing one opaque desktop image.
/// </summary>
/// <param name="Id">Stable id of the window inside the container (its HWND value).</param>
/// <param name="Title">Window title.</param>
/// <param name="X">Left edge in the container's virtual desktop coordinates.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Window width.</param>
/// <param name="Height">Window height.</param>
/// <param name="Sequence">Frame sequence this entry was last updated in.</param>
public sealed record ContainerDesktopWindow(
    long Id,
    string Title,
    int X,
    int Y,
    int Width,
    int Height,
    long Sequence);
