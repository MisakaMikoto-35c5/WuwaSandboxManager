using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.RdpHost;

/// <summary>
/// A request to present a live RDP session through the <c>mstscax</c> ActiveX
/// control.
/// <para>
/// The host acquires the RDPBASE named-pipe endpoint itself, on its own STA
/// thread. That is essential: the RDPBASE completion callback is only delivered
/// on an STA thread that pumps messages, so acquiring it from an MTA thread (a
/// console/thread-pool continuation) hangs forever.
/// </para>
/// </summary>
public sealed record RdpSessionRequest
{
    /// <summary>
    /// The owning sandbox instance id. Drives the lifecycle controls (power on,
    /// restart, shutdown) the connector exposes in its toolbar.
    /// </summary>
    public Guid SandboxId { get; init; }

    /// <summary>
    /// True to present the session as seamless RemoteApp (RAIL) windows. Mirrors
    /// <see cref="RemoteApp"/> but is kept alongside for the lifecycle controls.
    /// </summary>
    public SandboxConnectionMode Mode { get; init; } = SandboxConnectionMode.FullDesktop;

    /// <summary>The RDPBASE named-pipe endpoint, i.e. <c>\\.\pipe\&lt;VmId&gt;</c>.</summary>
    public required string PipeName { get; init; }

    /// <summary>
    /// The guest's TCP address (IPv4) to connect to instead of the named pipe.
    /// When set, the control connects over ordinary TCP RDP and the RDPBASE
    /// endpoint is never acquired. TCP is the multi-session-safe transport:
    /// the named pipe arbitrates a single interactive session across all
    /// sandboxes on the host.
    /// </summary>
    public string? ServerAddress { get; init; }

    /// <summary>The TCP port to use with <see cref="ServerAddress"/>.</summary>
    public int ServerPort { get; init; } = 3389;

    /// <summary>
    /// The compute system / VM id. Drives the named-pipe transport's
    /// <c>HiDefRemoteAppContainerGUID</c> routing on the control.
    /// </summary>
    public Guid VmId { get; init; }

    /// <summary>
    /// True to present seamless per-application windows (RAIL); false for a full
    /// guest desktop.
    /// </summary>
    public bool RemoteApp { get; init; }

    /// <summary>
    /// The program a RAIL session launches, e.g. <c>C:\Windows\explorer.exe</c>.
    /// The WDAG guest is RemoteApp-only and does not publish an application list,
    /// so a RAIL session must name the program to start. Ignored unless
    /// <see cref="RemoteApp"/> is true.
    /// </summary>
    public string? RemoteAppProgram { get; init; }

    public string WindowTitle { get; init; } = "Windows Sandbox";

    public bool FullScreen { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public SandboxRedirections Redirections { get; init; } = SandboxRedirections.Default;
}
