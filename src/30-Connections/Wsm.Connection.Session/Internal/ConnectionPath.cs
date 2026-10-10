namespace Wsm.Connection.Session.Internal;

/// <summary>Which presentation path <c>SessionLauncher</c> selected.</summary>
internal enum ConnectionPath
{
    /// <summary>
    /// Packaged client (or platform CLI) opening the existing sandbox by id
    /// (<c>wsb connect --id</c> / <c>WindowsSandboxRemoteSession.exe --id</c>).
    /// </summary>
    FullDesktop,

    /// <summary>Direct RDPBASE named-pipe endpoint hosted by mstscax (RAIL).</summary>
    RemoteApp,
}
