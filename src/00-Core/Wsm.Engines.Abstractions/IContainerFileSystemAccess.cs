using Wsm.Domain.ValueObjects;

namespace Wsm.Engines.Abstractions;

/// <summary>
/// Optional engine capability: host-visible access to a <b>running</b> container's
/// filesystem.
/// <para>
/// A process-isolated container's <c>C:\</c> is an activated scratch layer
/// mounted as a host volume (<c>\\?\Volume{GUID}\</c>), so the host can read and
/// write files in it directly. That is what lets a host component place a helper
/// into a container without any guest agent, and - because the host and the silo
/// then map the same file object - what makes a plain file inside the container a
/// legitimate shared-memory channel (see
/// <c>docs/investigation/13-container-desktop-and-rdp.md</c>, section 13).
/// </para>
/// </summary>
public interface IContainerFileSystemAccess
{
    /// <summary>
    /// The host path that is the container's <c>C:\</c> root (usually
    /// <c>\\?\Volume{GUID}\</c>), or <c>null</c> when the container is not
    /// running under this engine instance.
    /// </summary>
    string? GetContainerRoot(SandboxInstanceId id);
}
