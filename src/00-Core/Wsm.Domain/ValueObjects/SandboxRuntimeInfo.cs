namespace Wsm.Domain.ValueObjects;

/// <summary>
/// Live, backend produced runtime information for a running sandbox.
/// Mirrors the <c>RdpClientConfig</c> returned by <c>WindowsSandboxServer</c>.
/// </summary>
public sealed record SandboxRuntimeInfo
{
    public SandboxInstanceId SandboxId { get; init; }

    /// <summary>The Hyper-V container ("ManagedWindowsVM") identity.</summary>
    public Guid VmId { get; init; }

    public RdpTransportKind Transport { get; init; }

    public bool RailMode { get; init; }

    public bool FullScreen { get; init; }

    public string WindowTitle { get; init; } = "Windows Sandbox";

    public string UserName { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public string IpAddress { get; init; } = string.Empty;

    public IReadOnlyList<string> IpAddresses { get; init; } = Array.Empty<string>();

    public SandboxRedirections Redirections { get; init; } = SandboxRedirections.Default;

    /// <summary>
    /// The RDP endpoint used for the named-pipe transport,
    /// i.e. <c>\\.\pipe\&lt;VmId&gt;</c>.
    /// </summary>
    public string NamedPipeEndpoint => $@"\\.\pipe\{VmId:D}";

    public override string ToString()
        => $"vm={VmId:D} transport={Transport} rail={RailMode} user={UserName}";
}

/// <summary>Why a sandbox VM terminated (mirrors the server enum).</summary>
public enum SandboxTerminationReason
{
    None = 0,
    UserInitiatedShutdown = 1,
    UserInitiatedShutdownForReboot = 2,
    UnexpectedExit = 3,
    HostShutdown = 4,
    Unknown = 99,
}
