namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// Stable ids of the built-in container desktop providers. A recipe may name one of
/// these (or any id a future module registers) in
/// <c>SandboxDesktopPlan.Provider</c>.
/// </summary>
public static class ContainerDesktopProviderIds
{
    /// <summary>In-container agent + one shared frame file + one host window.</summary>
    public const string FrameBuffer = "frame-buffer";

    /// <summary>Winlogon wrap + in-session read side + one host window per container window.</summary>
    public const string WindowMirror = "window-mirror";
}
