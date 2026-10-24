namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// How a container desktop channel shares its frames. The mode is negotiated per
/// channel: the provider writes <c>shm-mode.txt</c> into the agent directory before
/// the agent starts, the agent publishes its choice in the header's mode field, and
/// the readers (providers, viewers) follow it.
/// </summary>
public enum ContainerDesktopMode
{
    /// <summary>
    /// The frames live in a file on the container's scratch volume
    /// (<c>frame.shm</c> / <c>mirror.shm</c>) that both sides map. Always works, but
    /// every frame is dirty NTFS pages the host has to write back.
    /// </summary>
    File = 0,

    /// <summary>
    /// The frames live in a <b>page-file-backed section</b> the in-container writer
    /// creates (<c>CreateFileMappingW(INVALID_HANDLE_VALUE, …)</c>); the
    /// <c>.shm</c> file then carries only the 4 KB header, and a host reader maps the
    /// section by duplicating its handle out of the writer process (needs elevation -
    /// see <c>SectionPassthrough</c>). No filesystem writeback.
    /// </summary>
    Pagefile = 1,
}
