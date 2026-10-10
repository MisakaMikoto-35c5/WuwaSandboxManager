using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.Session;

/// <summary>
/// Opens the user-visible RDP session for a running sandbox.
/// <para>
/// The full-desktop path opens the existing sandbox <b>by id</b>: the platform
/// CLI <c>wsb.exe connect --id &lt;id&gt;</c> is preferred, with the packaged
/// <c>WindowsSandboxRemoteSession.exe --id &lt;id&gt;</c> as the equivalent
/// fallback. The RAIL/RemoteApp ("individual windows") presentation is chosen by
/// the <c>RailMode</c> flag baked into the sandbox when it was started, and can
/// additionally be presented directly over the RDPBASE named-pipe endpoint.
/// </para>
/// </summary>
public interface ISessionLauncher
{
    Task LaunchAsync(
        SandboxInstanceId id,
        SandboxRuntimeInfo runtime,
        SandboxConnectionMode mode,
        CancellationToken cancellationToken = default);
}
