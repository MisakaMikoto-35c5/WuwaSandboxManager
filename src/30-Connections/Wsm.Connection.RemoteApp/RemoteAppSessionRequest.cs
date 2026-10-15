using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.RemoteApp;

/// <summary>
/// A request to present a sandbox <b>RemoteApp (RAIL)</b> session through the
/// stock Windows Remote Desktop client (<c>mstsc.exe</c>), driven by a generated
/// <c>.rdp</c> document.
/// <para>
/// The client can only dial TCP. Supply <see cref="ServerAddress"/> when the
/// guest is reachable over TCP; otherwise supply <see cref="PipeName"/> and the
/// host bridges the sandbox's named-pipe RDP endpoint to a loopback TCP port.
/// </para>
/// </summary>
public sealed record RemoteAppSessionRequest
{
    /// <summary>The sandbox the session belongs to (session bookkeeping/temp files).</summary>
    public required Guid SandboxId { get; init; }

    /// <summary>
    /// The guest address (IPv4 or host name) to connect to over TCP, or
    /// <c>null</c>/empty to use the named-pipe transport (<see cref="PipeName"/>).
    /// </summary>
    public string? ServerAddress { get; init; }

    /// <summary>The TCP port used with <see cref="ServerAddress"/>.</summary>
    public int ServerPort { get; init; } = 3389;

    /// <summary>
    /// The RDP named-pipe endpoint, i.e. <c>\\.\pipe\&lt;VmId&gt;</c>. Used when
    /// <see cref="ServerAddress"/> is not set; the pipe is bridged to a loopback
    /// TCP port so <c>mstsc</c> can drive the session without guest networking.
    /// </summary>
    public string? PipeName { get; init; }

    /// <summary>
    /// The program the RAIL session launches in the guest, e.g.
    /// <c>C:\Windows\explorer.exe</c>. Rendered as
    /// <c>remoteapplicationprogram</c>.
    /// </summary>
    public string Program { get; init; } = SandboxRecipe.DefaultRailProgram;

    /// <summary>
    /// Optional command line for <see cref="Program"/>, rendered as
    /// <c>remoteapplicationcmdline</c>.
    /// </summary>
    public string? ProgramArguments { get; init; }

    /// <summary>
    /// The display name of the remote application, shown by the client while it
    /// starts (and on the connection bar). Rendered as <c>remoteapplicationname</c>.
    /// </summary>
    public string DisplayName { get; init; } = "RemoteApp";

    /// <summary>Start the application window maximized (<c>screen mode id:i:2</c>).</summary>
    public bool FullScreen { get; init; }

    public string UserName { get; init; } = string.Empty;

    /// <summary>
    /// The clear-text guest password. It is never written in the clear: the
    /// <c>.rdp</c> carries the DPAPI-protected hex blob (<c>password 51:b:</c>).
    /// </summary>
    public string Password { get; init; } = string.Empty;

    public SandboxRedirections Redirections { get; init; } = SandboxRedirections.Default;
}
