namespace Wsm.Connection.RemoteApp;

/// <summary>
/// Presents a sandbox RemoteApp (RAIL) session through the stock Remote Desktop
/// client (<c>mstsc.exe</c>), which <b>replaces</b> the in-process
/// <c>mstscax</c> windowed connector for the remoteapptool-style workflow. The
/// default RAIL path is the WDAG-style client
/// (<c>Wsm.Connection.RailClient</c>); this kit remains as the alternative that
/// produces a hand-openable <c>.rdp</c>.
/// <para>
/// The default implementation generates a <c>.rdp</c> document, bridges the
/// sandbox named pipe to loopback TCP when needed, and starts one
/// <c>mstsc.exe</c> process per session.
/// </para>
/// </summary>
public interface IRemoteAppSessionHost
{
    /// <summary>
    /// Starts the RAIL session and completes when the client exits (the user
    /// closed the application window, the session dropped, or
    /// <see cref="CloseAll"/> ended it).
    /// </summary>
    Task ShowAsync(RemoteAppSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends every live RemoteApp session: asks each <c>mstsc.exe</c> window to
    /// close (a graceful client disconnect) and kills the process when it does
    /// not exit in time. Called before a sandbox is stopped so a client cannot
    /// hold — or immediately re-establish — the guest session while the compute
    /// system shuts down.
    /// </summary>
    void CloseAll();
}
