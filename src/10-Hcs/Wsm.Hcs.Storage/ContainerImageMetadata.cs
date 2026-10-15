using System.Text.Json;

namespace Wsm.Hcs.Storage;

/// <summary>
/// Side-car metadata written next to an imported WCOW base image layer
/// (<c>wsm-image.json</c>) recording the OS build and SKU it was imported from.
/// HCS refuses a process-isolated container whose image build does not match the
/// host (except for a Windows Server image on a Windows 11 host), so the
/// isolation policy reads this instead of loading the image's registry hive.
/// </summary>
public sealed record ContainerImageMetadata
{
    /// <summary>The file name written inside the layer folder.</summary>
    public const string FileName = "wsm-image.json";

    /// <summary>The image's OS build (for example <c>20348</c> for Windows Server 2022).</summary>
    public required int Build { get; init; }

    /// <summary>True when the image is a Windows Server SKU (not a client image).</summary>
    public required bool IsServer { get; init; }

    /// <summary>The image reference it was imported from (for display).</summary>
    public string? SourceReference { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Writes the metadata into <paramref name="layerPath"/>.</summary>
    public void Write(string layerPath)
    {
        Directory.CreateDirectory(layerPath);
        File.WriteAllText(Path.Combine(layerPath, FileName), JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>
    /// Reads the metadata for a layer folder, or null when it is absent/unreadable.
    /// Falls back to deriving the build from the layer's <c>ntoskrnl.exe</c> and
    /// treating the SKU as client (safe: it then requires an exact build match).
    /// </summary>
    public static ContainerImageMetadata? TryRead(string layerPath)
    {
        if (string.IsNullOrWhiteSpace(layerPath))
        {
            return null;
        }

        try
        {
            var file = Path.Combine(layerPath, FileName);
            if (File.Exists(file))
            {
                return JsonSerializer.Deserialize<ContainerImageMetadata>(File.ReadAllText(file));
            }
        }
        catch
        {
            // Fall through to the ntoskrnl probe.
        }

        var build = TryReadBuildFromKernel(layerPath);
        return build is null ? null : new ContainerImageMetadata { Build = build.Value, IsServer = false };
    }

    /// <summary>
    /// Reads the <c>Major.Minor.Build</c> of a layer's <c>ntoskrnl.exe</c>
    /// (e.g. <c>10.0.20348.4294</c> → <c>20348</c>). Works for a plain extracted
    /// layer without loading a registry hive or requiring elevation.
    /// </summary>
    public static int? TryReadBuildFromKernel(string layerPath)
    {
        try
        {
            var kernel = Path.Combine(layerPath, "Files", "Windows", "System32", "ntoskrnl.exe");
            if (!File.Exists(kernel))
            {
                return null;
            }

            var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(kernel).FileVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            var parts = version.Split('.');
            return parts.Length >= 3 && int.TryParse(parts[2], out var build) ? build : null;
        }
        catch
        {
            return null;
        }
    }
}
