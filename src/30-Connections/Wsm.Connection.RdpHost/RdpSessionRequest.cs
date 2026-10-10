using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.RdpHost;

/// <summary>
/// A request to present a live RDP session through the <c>mstscax</c>
/// (<c>MsRdpClient10</c>) ActiveX control. The <see cref="Endpoint"/> is the RDP
/// network stream acquired from the RDPBASE named-pipe connector and is fed to
/// the control via <c>AdvancedSettings.ConnectWithEndpoint</c>.
/// </summary>
public sealed record RdpSessionRequest
{
    /// <summary>The RDP net-stream endpoint produced by <c>IRDPENCNamedPipeDirectConnector</c>.</summary>
    public required object Endpoint { get; init; }

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
