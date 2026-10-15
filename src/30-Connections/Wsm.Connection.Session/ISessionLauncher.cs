using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.Session;

/// <summary>
/// Opens the user-visible session for a running sandbox, routing by connection
/// mode:
/// <list type="bullet">
/// <item>
/// <b>RemoteApp (RAIL)</b>: presented as a seamless per-application session
/// through the <b>stock Remote Desktop client</b> (<c>mstsc.exe</c>) and a
/// generated <c>.rdp</c> document — the <c>remoteapptool</c> mechanism. A
/// sandbox whose RDP endpoint is a named pipe is bridged to loopback TCP first,
/// so the client never needs guest networking.
/// </item>
/// <item>
/// <b>Full desktop</b>: presented over the sandbox's native transport (named
/// pipe or TCP) through the in-process <c>mstscax</c> windowed connector
/// (<c>Wsm.Connection.RdpHost</c>).
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
        string? remoteAppArguments = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends every open presentation session (graceful client disconnect, not a
    /// process kill). Called before a sandbox is stopped so a session host with
    /// auto-reconnect cannot hold the guest session — or reconnect into it —
    /// while the compute system is shutting down.
    /// </summary>
    void CloseAll();
}
