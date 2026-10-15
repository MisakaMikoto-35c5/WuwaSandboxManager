using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// Starts, drives and presents <b>container desktops</b> - the only way to see
/// and operate the desktop of a process-isolated (Argon) container.
/// <para>
/// This is deliberately the same shape as the rest of the connection layer: the
/// host orchestrates, the caller asks for a session and can present it. The
/// measurement that forces the shape is in
/// <c>docs/investigation/13-container-desktop-and-rdp.md</c>: <c>termsrv</c> never
/// creates an RDP listener inside a container, and a container's own window
/// cannot cross the silo boundary, so frames and input are the only channels and
/// the window has to be created in the user's session by a privileged host.
/// </para>
/// </summary>
public interface IContainerDesktopHost
{
    /// <summary>Whether the capability is enabled on this host.</summary>
    bool IsSupported { get; }

    /// <summary>Ids of the registered providers, for diagnostics and CLI help.</summary>
    IReadOnlyList<string> ProviderIds { get; }

    /// <summary>
    /// The provider a sandbox's manifest selects. This is the only place the
    /// mechanism is chosen, which is what makes providers replaceable.
    /// </summary>
    IContainerDesktopProvider ResolveProvider(SandboxRecipe recipe);

    /// <summary>All live desktop channels.</summary>
    IReadOnlyList<ContainerDesktopSession> Sessions { get; }

    /// <summary>Gets a live channel, or <c>null</c>.</summary>
    ContainerDesktopSession? Get(SandboxInstanceId id);

    /// <summary>
    /// Starts a desktop channel for a running container: materialises the agent,
    /// launches it inside the container and waits for its first frame.
    /// </summary>
    Task<ContainerDesktopSession> StartAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a desktop for a running container using the provider its
    /// <b>manifest</b> selects, and presents it when the plan asks for that.
    /// </summary>
    Task<ContainerDesktopSession> StartAsync(
        SandboxInstanceId id, SandboxRecipe recipe, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a desktop channel against an already-running compute system and the
    /// host-visible root of its filesystem. Used when the sandbox is not managed
    /// by this process (and by tooling/tests).
    /// </summary>
    Task<ContainerDesktopSession> AttachAsync(string computeSystemId, string containerRoot, CancellationToken cancellationToken = default);

    /// <summary>As <see cref="AttachAsync(string, string, CancellationToken)"/>, with an explicit manifest.</summary>
    Task<ContainerDesktopSession> AttachAsync(
        string computeSystemId, string containerRoot, SandboxRecipe recipe,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens the channel's frames in a window <b>inside the interactive user
    /// session</b> (SYSTEM/service callers get it on the console via
    /// <c>WTSQueryUserToken</c> + <c>CreateProcessAsUser</c>).
    /// </summary>
    Task<ContainerDesktopSession> PresentAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    /// <summary>Sends one input command (<c>move</c>/<c>click</c>/<c>keys</c>/<c>text</c>) to the container.</summary>
    Task SendInputAsync(SandboxInstanceId id, string command, CancellationToken cancellationToken = default);

    /// <summary>Stops the channel: the agent exits and the session is forgotten.</summary>
    Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks a container's agent to exit without a live session record, so a
    /// short-lived caller (one CLI invocation) can stop an agent another caller
    /// started. Best effort.
    /// </summary>
    Task StopAsync(string computeSystemId, string containerRoot, CancellationToken cancellationToken = default);
}
