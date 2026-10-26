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

    /// <summary>Unrecognized text supplied through the legacy string constructor.</summary>
    Unknown,
}

/// <summary>
/// Progress of an image import: a localizable <see cref="StageKind"/>, the legacy
/// string <see cref="Stage"/>, an optional download fraction and byte counters.
/// </summary>
public sealed record ContainerImageImportProgress
{
    /// <summary>Preserves the original string-based progress contract.</summary>
    public ContainerImageImportProgress(string Stage, double? Fraction = null)
    {
        this.Stage = Stage;
        this.Fraction = Fraction;
        StageKind = ParseLegacyStage(Stage);
    }

    /// <summary>Creates progress with a stage that callers can localize.</summary>
    public ContainerImageImportProgress(
        ContainerImageImportStage Stage,
        double? Fraction = null,
        long? BytesReceived = null,
        long? TotalBytes = null)
    {
        this.Stage = LegacyStageText(Stage);
        this.Fraction = Fraction;
        this.BytesReceived = BytesReceived;
        this.TotalBytes = TotalBytes;
        StageKind = Stage;
    }

    /// <summary>The original stage text supplied by legacy callers.</summary>
    public string Stage { get; init; }

    /// <summary>The structured stage used by new callers for localization.</summary>
    public ContainerImageImportStage StageKind { get; init; }

    /// <summary>The completed fraction in <c>[0,1]</c>, when known.</summary>
    public double? Fraction { get; init; }

    /// <summary>Bytes downloaded so far, when available.</summary>
    public long? BytesReceived { get; init; }

    /// <summary>Total bytes to download, when available.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>The legacy English stage text.</summary>
    public string Message => Stage;

    /// <summary>Preserves deconstruction by original string-stage callers.</summary>
    public void Deconstruct(out string stage, out double? fraction)
    {
        stage = Stage;
        fraction = Fraction;
    }

    /// <summary>Preserves deconstruction by enum-stage callers.</summary>
    public void Deconstruct(
        out ContainerImageImportStage stage,
        out double? fraction,
        out long? bytesReceived,
        out long? totalBytes)
    {
        stage = StageKind;
        fraction = Fraction;
        bytesReceived = BytesReceived;
        totalBytes = TotalBytes;
    }

    private static ContainerImageImportStage ParseLegacyStage(string stage)
    {
        switch (stage)
        {
            case "Resolving the image manifest": return ContainerImageImportStage.ResolvingManifest;
            case "Downloading the base layer": return ContainerImageImportStage.Downloading;
            case "Extracting the layer": return ContainerImageImportStage.Extracting;
            case "Preparing the layer": return ContainerImageImportStage.Preparing;
            case "Done": return ContainerImageImportStage.Done;
        }

        return Enum.TryParse<ContainerImageImportStage>(stage, ignoreCase: true, out var parsed)
            ? parsed
            : ContainerImageImportStage.Unknown;
    }

    private static string LegacyStageText(ContainerImageImportStage stage) => stage switch
    {
        ContainerImageImportStage.ResolvingManifest => "Resolving the image manifest",
        ContainerImageImportStage.Downloading => "Downloading the base layer",
        ContainerImageImportStage.Extracting => "Extracting the layer",
        ContainerImageImportStage.Preparing => "Preparing the layer",
        ContainerImageImportStage.Done => "Done",
        _ => stage.ToString(),
    };
}
