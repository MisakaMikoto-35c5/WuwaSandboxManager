namespace Wsm.Domain.ValueObjects;

/// <summary>
/// How the desktop of a <b>process-isolated container</b> is produced and
/// presented. Process isolation has no RDP and no way to move a window across the
/// silo, so a desktop always needs a transport; this selects which one.
/// <para>
/// See <c>docs/investigation/13-container-desktop-and-rdp.md</c>: section 13-14
/// describe <see cref="FrameBuffer"/>, section 15-18 describe
/// <see cref="WindowMirror"/>.
/// </para>
/// </summary>
public enum ContainerDesktopKind
{
    /// <summary>No desktop is produced for this sandbox.</summary>
    None = 0,

    /// <summary>
    /// An in-container agent captures the whole desktop into a shared frame file;
    /// the host shows it in one window. Simple and dependency-free, but it copies
    /// full frames.
    /// </summary>
    FrameBuffer = 1,

    /// <summary>
    /// The container's logon is wrapped so our read side runs <b>inside its
    /// interactive session</b>; it publishes per-window surfaces and dirty regions,
    /// and the host mirrors each container window as a real host window. Needs an
    /// image-level change, but copies far less and gives window-level input.
    /// </summary>
    WindowMirror = 2,
}
