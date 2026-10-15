namespace Wsm.Connection.ContainerDesktop;

/// <summary>Outcome of launching a process for a session.</summary>
/// <param name="Launched">Whether a process was created.</param>
/// <param name="ProcessId">Its process id (0 when not launched).</param>
/// <param name="SessionId">The session it ended up in.</param>
/// <param name="Interactive">Whether it landed in another session on purpose.</param>
/// <param name="Detail">Human-readable explanation for logs.</param>
public readonly record struct SessionProcessLaunchResult(
    bool Launched,
    int ProcessId,
    int SessionId,
    bool Interactive,
    string Detail);

/// <summary>
/// Starts a process for the caller, preferably <b>in the interactive console
/// session</b>. A service (SYSTEM) can do that - and must, otherwise its UI lands
/// in session 0 where nobody can see it; an ordinary user token cannot, because
/// <c>WTSQueryUserToken</c> needs <c>SeTcbPrivilege</c>, so the implementation
/// falls back to starting the process in the caller's own session.
/// </summary>
public interface ISessionProcessLauncher
{
    /// <summary>Whether this process can place a window in the interactive session.</summary>
    bool CanLaunchIntoInteractiveSession { get; }

    /// <summary>
    /// Launches <paramref name="commandLine"/>, in the interactive session when
    /// possible, and reports where it ended up.
    /// </summary>
    SessionProcessLaunchResult Launch(string commandLine, string? workingDirectory = null);
}
