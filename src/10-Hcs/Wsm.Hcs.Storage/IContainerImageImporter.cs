namespace Wsm.Hcs.Storage;

/// <summary>
/// Provisions a WCOW <b>base image layer</b> from a container image reference,
/// so the process-isolated container engine has an image to build containers
/// from.
/// <para>
/// The Windows Sandbox ship image is a <b>client</b> image; feature-update builds
/// can leave it older than the host, and HCS then refuses to run it process
/// isolated (<c>HCS_E_IMAGE_MISMATCH</c>). A Windows Server 2022/2025 image is
/// exempt from that rule on a Windows 11 host, so importing one (e.g.
/// <c>mcr.microsoft.com/windows/servercore:ltsc2022</c>) is the supported way to
/// run process isolation on a mismatched host.
/// </para>
/// </summary>
public interface IContainerImageImporter
{
    /// <summary>Windows Server images this build suggests importing (newest first).</summary>
    IReadOnlyList<string> SuggestedImages { get; }

    /// <summary>
    /// The suggested images with display metadata (name, approximate download
    /// size, description resource key) for a picker UI. Free-text references
    /// remain supported by <see cref="ImportBaseLayerAsync"/>; the catalog is
    /// only a convenience.
    /// </summary>
    IReadOnlyList<ContainerImageInfo> ImageCatalog { get; }

    /// <summary>
    /// Downloads the base OS layer of <paramref name="reference"/> (default
    /// <c>mcr.microsoft.com/windows/servercore:ltsc2022</c>), extracts it into
    /// <paramref name="layerPath"/> and prepares it as a WCOW layer. Returns the
    /// absolute layer path. Honours <paramref name="ct"/> and leaves no partial
    /// download or half-extracted layer behind when cancelled or failed.
    /// </summary>
    Task<string> ImportBaseLayerAsync(
        string reference,
        string layerPath,
        IProgress<ContainerImageImportProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// One suggested container image: what to show in a picker plus the reference
/// to import. <see cref="ApproxSizeGB"/> is informational only (registries
/// recompress; treat it as a hint for the user, never as a quota).
/// </summary>
public sealed record ContainerImageInfo
{
    /// <summary>Stable identifier (e.g. <c>windows-server-ltsc2025</c>).</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable name (e.g. <c>Windows Server 2025 (full)</c>).</summary>
    public required string DisplayName { get; init; }

    /// <summary>The registry reference to import.</summary>
    public required string Reference { get; init; }

    /// <summary>Approximate download size in GB, informational only.</summary>
    public required double ApproxSizeGB { get; init; }

    /// <summary>Resource key the caller resolves for a localized description.</summary>
    public required string DescriptionKey { get; init; }
}

/// <summary>The stage an image import is in; callers localize it.</summary>
public enum ContainerImageImportStage
{
    /// <summary>Resolving the registry manifest (no bytes yet).</summary>
    ResolvingManifest,

    /// <summary>Downloading the base layer blob.</summary>
    Downloading,

    /// <summary>Extracting the downloaded tarball.</summary>
    Extracting,

    /// <summary>Preparing the extracted layer as a WCOW base image.</summary>
    Preparing,

    /// <summary>The import finished.</summary>
    Done,
}

/// <summary>
/// Progress of an image import: the current <see cref="Stage"/>, the completed
/// fraction in <c>[0,1]</c> while downloading (null when unknown or not
/// applicable), and the byte counters while downloading (null when unknown).
/// </summary>
public sealed record ContainerImageImportProgress(
    ContainerImageImportStage Stage,
    double? Fraction = null,
    long? BytesReceived = null,
    long? TotalBytes = null)
{
    /// <summary>
    /// The legacy English stage text, kept for callers written against the
    /// original string-based record. New callers should localize
    /// <see cref="Stage"/> instead.
    /// </summary>
    public string Message => Stage switch
    {
        ContainerImageImportStage.ResolvingManifest => "Resolving the image manifest",
        ContainerImageImportStage.Downloading => "Downloading the base layer",
        ContainerImageImportStage.Extracting => "Extracting the layer",
        ContainerImageImportStage.Preparing => "Preparing the layer",
        ContainerImageImportStage.Done => "Done",
        _ => Stage.ToString(),
    };
}
