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
    /// <summary>The RDPBASE named-pipe endpoint, i.e. <c>\\.\pipe\&lt;VmId&gt;</c>.</summary>
    public required string PipeName { get; init; }

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

    public string WindowTitle { get; init; } = "Windows Sandbox";

    public bool FullScreen { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public SandboxRedirections Redirections { get; init; } = SandboxRedirections.Default;
}
