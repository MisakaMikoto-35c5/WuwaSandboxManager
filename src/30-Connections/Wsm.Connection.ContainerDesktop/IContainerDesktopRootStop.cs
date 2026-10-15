namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// Optional provider capability: ask a container's desktop channel to stop without a
/// live session record. Needed by short-lived callers (a single CLI invocation) that
/// did not start the session themselves.
/// </summary>
public interface IContainerDesktopRootStop
{
    /// <summary>Signals the channel under a container root to stop. Best effort.</summary>
    Task StopAsync(string computeSystemId, string containerRoot, CancellationToken cancellationToken = default);
}
