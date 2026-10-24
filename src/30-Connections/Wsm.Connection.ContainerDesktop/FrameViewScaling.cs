namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// The letterbox math of the frame viewer: a frame of fixed geometry is drawn scaled
/// into a resizable client area, and mouse coordinates are mapped back into frame
/// coordinates. Pure, so the viewer and the tests share one implementation.
/// </summary>
public static class FrameViewScaling
{
    /// <summary>
    /// The largest frame-aspect rectangle that fits the client area, centred. An empty
    /// frame or client yields an empty rectangle.
    /// </summary>
    public static FrameViewRect Fit(int frameWidth, int frameHeight, int clientWidth, int clientHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || clientWidth <= 0 || clientHeight <= 0)
        {
            return default;
        }

        var scale = Math.Min((double)clientWidth / frameWidth, (double)clientHeight / frameHeight);
        var width = Math.Max(1, (int)(frameWidth * scale));
        var height = Math.Max(1, (int)(frameHeight * scale));
        return new FrameViewRect((clientWidth - width) / 2, (clientHeight - height) / 2, width, height);
    }

    /// <summary>
    /// Inverse of <see cref="Fit"/>: a client-area point in frame coordinates, clamped
    /// to the frame (a point in the letterbox maps to the nearest edge).
    /// </summary>
    public static (int X, int Y) ToFrame(
        int clientX, int clientY, int frameWidth, int frameHeight, int clientWidth, int clientHeight)
    {
        var rect = Fit(frameWidth, frameHeight, clientWidth, clientHeight);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return (0, 0);
        }

        var x = (int)(((long)(clientX - rect.X) * frameWidth) / rect.Width);
        var y = (int)(((long)(clientY - rect.Y) * frameHeight) / rect.Height);
        return (Math.Clamp(x, 0, frameWidth - 1), Math.Clamp(y, 0, frameHeight - 1));
    }
}

/// <summary>The destination rectangle of a scaled frame inside the viewer's client area.</summary>
public readonly record struct FrameViewRect(int X, int Y, int Width, int Height);
