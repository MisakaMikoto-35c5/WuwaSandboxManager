namespace Wsm.Connection.RailClient;

/// <summary>
/// Presents a sandbox RemoteApp (RAIL) session the way WDAG did: a dedicated
/// client hosts the <c>mstscax</c> control over the RDPBASE named pipe and the
/// remote application windows float freely on the desktop (drag, stack,
/// minimize like local windows).
/// <para>
/// Each session is expected to run in its own client process (the GUI starts a
/// <c>wsm connect</c> child per session, mirroring the platform's
/// one-client-per-sandbox model): the in-process <c>mstscax</c> runtime keeps
/// only the first session of a process alive.
/// </para>
/// </summary>
public interface IRailClientSessionHost
{
    /// <summary>
    /// Starts the RAIL client and completes when the session ends (the remote
    /// application closed, the connection dropped, or <see cref="CloseAll"/>
    /// ended it).
    /// </summary>
    Task ShowAsync(RailClientSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends every live RAIL session: closes the status/broker window (a graceful
    /// client disconnect). Called before a sandbox is stopped so a client cannot
    /// hold — or immediately re-establish — the guest session while the compute
    /// system shuts down.
    /// </summary>
    void CloseAll();
}
