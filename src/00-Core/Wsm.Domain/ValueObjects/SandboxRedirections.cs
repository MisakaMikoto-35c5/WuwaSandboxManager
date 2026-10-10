namespace WindowsSandboxManager.Domain.ValueObjects;

/// <summary>
/// The device/redirection surface exposed to the guest over the RDP session.
/// These mirror the .wsb elements and the <c>RdpClientConfig</c> produced by
/// the server (clipboard, printer, audio/video input, smart card).
/// </summary>
public sealed record SandboxRedirections
{
    public bool Clipboard { get; init; } = true;
    public bool Printer { get; init; }
    public bool AudioInput { get; init; } = true;
    public bool VideoInput { get; init; }
    public bool SmartCard { get; init; }

    public static SandboxRedirections Default { get; } = new();

    public static SandboxRedirections Everything { get; } = new()
    {
        Clipboard = true,
        Printer = true,
        AudioInput = true,
        VideoInput = true,
        SmartCard = true,
    };
}
