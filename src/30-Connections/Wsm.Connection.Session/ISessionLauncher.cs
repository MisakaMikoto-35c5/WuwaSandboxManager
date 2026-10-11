using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.Session;

/// <summary>
/// Opens the user-visible RDP session for a running sandbox.
/// <para>
/// The path depends on the <see cref="SandboxEngineKind"/> of the sandbox:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Windows Sandbox</b>: the full-desktop path opens the existing sandbox
/// <b>by id</b> through the packaged connector — the platform CLI
/// <c>wsb.exe connect --id &lt;id&gt;</c>, then the packaged
/// <c>WindowsSandboxRemoteSession.exe --id &lt;id&gt;</c>, then the legacy
/// launcher. RAIL/RemoteApp is presented directly over the RDPBASE named-pipe
/// endpoint.
/// </item>
/// <item>
/// <b>Any other engine</b> (HCS, container): the by-id connector cannot resolve
/// the sandbox (it is not registered with <c>WindowsSandboxServer</c>), so the
/// connection is a plain RDP session over the RDPBASE named-pipe endpoint
/// (<c>\\.\pipe&lt;VmId&gt;</c>) hosted by <c>mstscax</c>, with no packaged-client
/// fallback.
/// </item>
/// </list>
/// </summary>
public interface ISessionLauncher
{
    Task LaunchAsync(
        SandboxInstanceId id,
        SandboxRuntimeInfo runtime,
        SandboxConnectionMode mode,
        SandboxEngineKind engineKind,
        string? remoteAppProgram = null,
        CancellationToken cancellationToken = default);
}
