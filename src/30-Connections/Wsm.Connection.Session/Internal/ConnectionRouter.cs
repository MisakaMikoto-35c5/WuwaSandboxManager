using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.Session.Internal;

/// <summary>
/// Decides whether a connection should use the full-desktop path (packaged
/// client opening the sandbox by id) or the direct RAIL path (RDPBASE named-pipe
/// endpoint hosted by <c>mstscax</c>).
/// <para>
/// The presentation is fixed when the sandbox is started
/// (<see cref="SandboxRecipe.RailMode"/>), so a RAIL sandbox always routes to the
/// direct path even if the caller asks for a full desktop, while an explicit
/// RemoteApp request on a full-desktop sandbox also routes to the direct path
/// (which can present RAIL windows).
/// </para>
/// </summary>
internal static class ConnectionRouter
{
    public static ConnectionPath Route(SandboxConnectionMode mode, SandboxRuntimeInfo runtime)
        => mode == SandboxConnectionMode.RemoteApp || runtime.RailMode
            ? ConnectionPath.RemoteApp
            : ConnectionPath.FullDesktop;
}
