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
    /// Downloads the base OS layer of <paramref name="reference"/> (default
    /// <c>mcr.microsoft.com/windows/servercore:ltsc2022</c>), extracts it into
    /// <paramref name="layerPath"/> and prepares it as a WCOW layer. Returns the
    /// absolute layer path.
    /// </summary>
    Task<string> ImportBaseLayerAsync(
        string reference,
        string layerPath,
        IProgress<ContainerImageImportProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>
/// Progress of an image import: a human-readable stage and, while downloading,
/// the completed fraction in <c>[0,1]</c> (null when the size is unknown).
/// </summary>
public sealed record ContainerImageImportProgress(string Stage, double? Fraction = null);
