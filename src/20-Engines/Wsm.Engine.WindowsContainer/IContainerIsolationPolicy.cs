namespace Wsm.Engine.WindowsContainer;

/// <summary>
/// The outcome of evaluating whether <b>process isolation</b> (an HCS Argon
/// container) can run on this host with the currently selected image.
/// <para>
/// HCS refuses a process-isolated container whose image OS build does not match
/// the host's. The single exception Microsoft documents is a <b>Windows Server
/// 2022/2025</b> image on a Windows 11 host, which may run despite the mismatch.
/// A Windows Sandbox (client) image therefore requires an exact build match.
/// </para>
/// </summary>
public sealed record ContainerIsolationSupport
{
    /// <summary>Whether process isolation can be offered with the current image.</summary>
    public required bool IsSupported { get; init; }

    /// <summary>
    /// A localization key explaining the outcome (for example
    /// <c>Container.Reason.SupportedServer</c>). The UI resolves it.
    /// </summary>
    public required string ReasonKey { get; init; }

    /// <summary>The host OS build (from the registry).</summary>
    public required int HostBuild { get; init; }

    /// <summary>True when the host is a Windows 11 client (build 22000+).</summary>
    public required bool HostIsWindows11Client { get; init; }

    /// <summary>The image's OS build, when it could be determined.</summary>
    public int? ImageBuild { get; init; }

    /// <summary>True when the image is a Windows Server SKU.</summary>
    public bool ImageIsServer { get; init; }

    /// <summary>The image base layer path that was evaluated.</summary>
    public string? ImagePath { get; init; }
}

/// <summary>
/// Decides whether the process-isolation engine may be offered, per Microsoft's
/// container version-compatibility rules, and explains why.
/// </summary>
public interface IContainerIsolationPolicy
{
    /// <summary>Evaluates the current host and image (cached between calls).</summary>
    ContainerIsolationSupport Evaluate();

    /// <summary>Drops the cache, e.g. after an image is imported.</summary>
    void Invalidate();
}
