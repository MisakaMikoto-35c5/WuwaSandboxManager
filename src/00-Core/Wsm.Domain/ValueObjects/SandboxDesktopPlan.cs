namespace Wsm.Domain.ValueObjects;

/// <summary>
/// The desktop plan of a sandbox: <b>which</b> transport produces the container's
/// desktop (and how it is configured). It lives in the sandbox recipe, so a recipe
/// can pick and tune the mechanism without any code change - and a new mechanism can
/// be added later as another provider without touching the recipe format's shape.
/// </summary>
public sealed record SandboxDesktopPlan
{
    /// <summary>
    /// The transport to use. <see cref="ContainerDesktopKind.FrameBuffer"/> is the
    /// default because it needs no image change at all.
    /// </summary>
    public ContainerDesktopKind Kind { get; init; } = ContainerDesktopKind.FrameBuffer;

    /// <summary>
    /// Configure the image's interactive console logon and persistent shell. Off by
    /// default so existing headless containers keep their startup and account behavior.
    /// </summary>
    public bool InteractiveSession { get; init; }

    /// <summary>
    /// Explicit provider id (e.g. <c>window-mirror</c>), which takes precedence over
    /// <see cref="Kind"/>. This is the seam that keeps providers replaceable: a
    /// recipe can name a provider the core does not know about yet.
    /// </summary>
    public string? Provider { get; init; }

    /// <summary>
    /// Show the desktop in a window inside the user's interactive session (a service
    /// host is required for that) instead of only publishing frames.
    /// </summary>
    public bool PresentToInteractiveSession { get; init; } = true;

    /// <summary>Send only what changed (per-window dirty regions) when supported.</summary>
    public bool DirtyRegions { get; init; } = true;

    /// <summary>Captured desktop width in pixels (0 = provider default).</summary>
    public int Width { get; init; }

    /// <summary>Captured desktop height in pixels (0 = provider default).</summary>
    public int Height { get; init; }

    /// <summary>Target frame rate (0 = provider default).</summary>
    public int Fps { get; init; }

    public static SandboxDesktopPlan Default { get; } = new();

    /// <summary>A plan that produces no desktop at all.</summary>
    public static SandboxDesktopPlan Off { get; } = new() { Kind = ContainerDesktopKind.None };
}
