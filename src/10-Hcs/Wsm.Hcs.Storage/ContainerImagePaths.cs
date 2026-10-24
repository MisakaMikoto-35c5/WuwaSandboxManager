namespace Wsm.Hcs.Storage;

/// <summary>
/// Where imported container image layers live by default, and whether a layer
/// folder already holds a usable image. Shared by the GUI and the CLI so both
/// pick the same folder for the same image reference.
/// </summary>
public static class ContainerImagePaths
{
    /// <summary>The default root every imported image is placed under.</summary>
    public static string GetDefaultRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsSandboxManager",
            "images");

    /// <summary>
    /// The default layer folder for <paramref name="reference"/>:
    /// <c>%LOCALAPPDATA%\WindowsSandboxManager\images\&lt;slug&gt;</c>.
    /// </summary>
    public static string GetDefaultLayerPath(string? reference)
        => Path.Combine(GetDefaultRoot(), Slugify(reference));

    /// <summary>
    /// Roots outside <see cref="GetDefaultRoot"/> that manually imported images
    /// were historically placed in; scanned in addition to the default root.
    /// </summary>
    public static IReadOnlyList<string> LegacyImportRoots { get; } = [@"C:\wsm-images"];

    /// <summary>
    /// True when an image repository or an import folder name denotes a Windows
    /// Server SKU (the ones HCS exempts from the host/image build-match rule on
    /// a Windows 11 host). Shared by the importer and the import scan.
    /// </summary>
    public static bool IsServerImageName(string name)
        => name.Contains("servercore", StringComparison.OrdinalIgnoreCase)
           || name.Contains("nanoserver", StringComparison.OrdinalIgnoreCase)
           || name.Contains("server", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Scans the default root and the <see cref="LegacyImportRoots"/> for
    /// already-imported base image layers. A folder counts when it holds a
    /// <c>Files\Windows</c> tree, the <c>Hives</c> links left by
    /// <c>ProcessBaseImage</c> (measured: <c>CreateSandboxLayer</c> answers
    /// <c>0x80070003</c> for a parent layer without them, so a half-imported
    /// folder is not a usable base) and a readable build/SKU
    /// (<see cref="ContainerImageMetadata"/>); when only the kernel fallback can
    /// read the build, the SKU is inferred from the folder name and an upgraded
    /// <c>wsm-image.json</c> is written back best-effort. Candidates are ordered
    /// server-first, then newest build first.
    /// </summary>
    public static IReadOnlyList<ContainerImageCandidate> FindImportedImages(IEnumerable<string>? roots = null)
    {
        var candidates = new List<ContainerImageCandidate>();
        foreach (var root in roots ?? GetDefaultScanRoots())
        {
            IEnumerable<string> folders;
            try
            {
                folders = Directory.Exists(root) ? Directory.EnumerateDirectories(root) : [];
            }
            catch
            {
                continue;
            }

            foreach (var folder in folders)
            {
                if (TryReadCandidate(folder) is { } candidate)
                {
                    candidates.Add(candidate);
                }
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Metadata.IsServer)
            .ThenByDescending(candidate => candidate.Metadata.Build)
            .ToArray();
    }

    private static IEnumerable<string> GetDefaultScanRoots()
    {
        yield return GetDefaultRoot();
        foreach (var root in LegacyImportRoots)
        {
            yield return root;
        }
    }

    private static ContainerImageCandidate? TryReadCandidate(string folder)
    {
        try
        {
            if (!Directory.Exists(Path.Combine(folder, "Files", "Windows"))
                || !Directory.Exists(Path.Combine(folder, "Hives")))
            {
                return null;
            }

            var metadata = ContainerImageMetadata.TryRead(folder);
            if (metadata is null)
            {
                return null;
            }

            // The kernel fallback cannot tell the SKU, so a server image
            // imported by hand would look like a client one; infer the SKU from
            // the folder name and remember the upgrade for the next scan.
            metadata = EnrichMetadataFromFolderName(folder, metadata);

            return new ContainerImageCandidate(folder, metadata);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// When the build came from the kernel fallback (no <c>wsm-image.json</c>),
    /// infers the SKU from the folder name (<see cref="IsServerImageName"/>) and
    /// writes the enriched metadata back best-effort.
    /// </summary>
    internal static ContainerImageMetadata EnrichMetadataFromFolderName(
        string folder, ContainerImageMetadata metadata)
    {
        if (metadata.IsServer
            || File.Exists(Path.Combine(folder, ContainerImageMetadata.FileName))
            || !IsServerImageName(Path.GetFileName(folder)))
        {
            return metadata;
        }

        var enriched = metadata with { IsServer = true };
        try
        {
            enriched.Write(folder);
        }
        catch
        {
            // Best effort; the inference still applies to this scan.
        }

        return enriched;
    }

    /// <summary>
    /// Turns a reference into a single folder name
    /// (<c>mcr.microsoft.com/windows/server:ltsc2025</c> →
    /// <c>mcr.microsoft.com-windows-server-ltsc2025</c>).
    /// </summary>
    internal static string Slugify(string? reference)
        => string.IsNullOrWhiteSpace(reference)
            ? "image"
            : reference.Trim().Replace(':', '-').Replace('/', '-');

    /// <summary>
    /// Inspects a candidate layer folder: <see cref="ContainerImageInstallState.Installed"/>
    /// when it holds image metadata, <see cref="ContainerImageInstallState.Occupied"/>
    /// when it exists with other content (the importer would refuse it), and
    /// <see cref="ContainerImageInstallState.Missing"/> when it is absent or empty.
    /// </summary>
    public static ContainerImageInstallState GetInstallState(string? layerPath)
    {
        if (string.IsNullOrWhiteSpace(layerPath) || !Directory.Exists(layerPath))
        {
            return ContainerImageInstallState.Missing;
        }

        if (File.Exists(Path.Combine(layerPath, ContainerImageMetadata.FileName)))
        {
            return ContainerImageInstallState.Installed;
        }

        return Directory.EnumerateFileSystemEntries(layerPath).Any()
            ? ContainerImageInstallState.Occupied
            : ContainerImageInstallState.Missing;
    }
}

/// <summary>What a candidate image layer folder currently holds.</summary>
public enum ContainerImageInstallState
{
    /// <summary>The folder does not exist or is empty; an import can target it.</summary>
    Missing,

    /// <summary>The folder holds a completed import (<c>wsm-image.json</c>).</summary>
    Installed,

    /// <summary>The folder exists with content that is not an imported image.</summary>
    Occupied,
}

/// <summary>
/// An imported base image layer found by
/// <see cref="ContainerImagePaths.FindImportedImages"/>: the layer folder plus
/// the build/SKU read (or inferred) for it.
/// </summary>
public sealed record ContainerImageCandidate(string Path, ContainerImageMetadata Metadata);
