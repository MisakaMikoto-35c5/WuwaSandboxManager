using WindowsSandboxManager.Domain.ValueObjects;

namespace Wsm.Connection.Session;

/// <summary>
/// Opens the user-visible RDP session for a running sandbox.
/// <para>
/// The primary path launches the packaged <c>WindowsSandboxRemoteSession.exe</c>
/// client, which implements both the full-desktop ("Remote Desktop style") and
/// the RAIL/RemoteApp ("individual windows") presentations. The presentation is
/// chosen by the <c>RailMode</c> flag baked into the sandbox when it was started.
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
