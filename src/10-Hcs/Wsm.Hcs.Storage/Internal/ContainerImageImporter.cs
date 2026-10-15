using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;

namespace Wsm.Hcs.Storage.Internal;

/// <summary>
/// Default <see cref="IContainerImageImporter"/>. Pulls an OCI/Docker image
/// manifest from the registry over plain HTTP, downloads the <b>base OS</b>
/// layer and extracts it with the in-box <c>tar.exe</c>, then prepares the layer
/// through <see cref="IWcowLayerService.ProcessBaseImage"/> (which creates the
/// <c>Hives</c> links and the blank scratch VHDX HCS needs).
/// <para>
/// Only the base layer is imported. That is a runnable (if unpatched) Windows
/// Server image; the follow-up cumulative-update layer is optional for the
/// engine and intentionally skipped to keep the import small.
/// </para>
/// </summary>
[ExposeServices(typeof(IContainerImageImporter))]
internal sealed class ContainerImageImporter : IContainerImageImporter, ISingletonDependency
{
    private const string DefaultReference = "mcr.microsoft.com/windows/servercore:ltsc2022";

    /// <summary>
    /// The Windows Server images offered for process isolation. Both are exempt
    /// from HCS's host/image build-match rule on a Windows 11 host.
    /// </summary>
    private static readonly string[] Suggested =
    [
        "mcr.microsoft.com/windows/servercore:ltsc2022",
        "mcr.microsoft.com/windows/servercore:ltsc2025",
    ];

    private static readonly string[] ManifestListAccept =
    [
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    private readonly IWcowLayerService _layers;
    private readonly ILogger<ContainerImageImporter> _logger;

    public ContainerImageImporter(IWcowLayerService layers, ILogger<ContainerImageImporter> logger)
    {
        _layers = layers;
        _logger = logger;
    }

    public IReadOnlyList<string> SuggestedImages => Suggested;

    public async Task<string> ImportBaseLayerAsync(
        string reference,
        string layerPath,
        IProgress<ContainerImageImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var image = ImageReference.Parse(string.IsNullOrWhiteSpace(reference) ? DefaultReference : reference);
        var destination = Path.GetFullPath(layerPath);

        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new SandboxValidationException(
                $"The target layer directory '{destination}' is not empty; choose a new path.");
        }

        Directory.CreateDirectory(destination);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        // 1. Resolve the platform-specific manifest.
        progress?.Report(new ContainerImageImportProgress("Resolving the image manifest"));
        var manifest = await GetManifestAsync(http, image, ct);
        var (digest, size) = manifest.Layers.Count > 0
            ? (manifest.Layers[0].Digest, manifest.Layers[0].Size)
            : throw new SandboxBackendException($"Image '{image}' has no layers to import.", 0);

        _logger.LogInformation(
            "Importing base layer {Digest} ({SizeMb} MB) of {Image} into '{Destination}'.",
            digest, size / (1024 * 1024), image, destination);

        // 2. Download the base layer blob next to the destination.
        var tarball = destination.TrimEnd('\\', '/') + ".layer.tgz";
        await DownloadAsync(http, image, digest, tarball, progress, ct);

        try
        {
            // 3. Extract (Files/, UtilityVM/, ...) with the in-box tar.
            progress?.Report(new ContainerImageImportProgress("Extracting the layer"));
            ExtractWithTar(tarball, destination);

            // 4. Prepare it as a WCOW base layer (Hives links + blank VHDX).
            progress?.Report(new ContainerImageImportProgress("Preparing the layer"));
            _layers.ProcessBaseImage(destination);

            // 5. Record build/SKU so the isolation policy can evaluate it.
            var build = ContainerImageMetadata.TryReadBuildFromKernel(destination);
            if (build is { } value)
            {
                new ContainerImageMetadata
                {
                    Build = value,
                    IsServer = IsServerRepository(image.Repository),
                    SourceReference = image.ToString(),
                }.Write(destination);
            }
        }
        finally
        {
            TryDelete(tarball);
        }

        _logger.LogInformation("Base layer for {Image} is ready at '{Destination}'.", image, destination);
        progress?.Report(new ContainerImageImportProgress("Done", 1));
        return destination;
    }

    /// <summary>Server images are the ones HCS exempts from the build-match rule.</summary>
    internal static bool IsServerRepository(string repository)
        => repository.Contains("servercore", StringComparison.OrdinalIgnoreCase)
           || repository.Contains("nanoserver", StringComparison.OrdinalIgnoreCase)
           || repository.Contains("windows/server", StringComparison.OrdinalIgnoreCase);

    private static async Task<ImageManifest> GetManifestAsync(HttpClient http, ImageReference image, CancellationToken ct)
    {
        var url = $"https://{image.Registry}/v2/{image.Repository}/manifests/{image.Tag}";
        using var rootRequest = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var accept in ManifestListAccept)
        {
            rootRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        }

        using var rootResponse = await http.SendAsync(rootRequest, ct);
        rootResponse.EnsureSuccessStatusCode();
        var rootJson = await rootResponse.Content.ReadAsStringAsync(ct);
        using var root = JsonDocument.Parse(rootJson);

        // A manifest list (multi-arch) points at the platform manifest.
        var mediaType = root.RootElement.TryGetProperty("mediaType", out var mt) ? mt.GetString() : null;
        if (mediaType is not null && mediaType.Contains("manifest.list", StringComparison.OrdinalIgnoreCase)
            || root.RootElement.TryGetProperty("manifests", out _))
        {
            var digest = SelectWindowsAmd64(root.RootElement)
                ?? throw new SandboxBackendException($"Image '{image}' has no windows/amd64 manifest.", 0);

            using var platformRequest = new HttpRequestMessage(HttpMethod.Get,
                $"https://{image.Registry}/v2/{image.Repository}/manifests/{digest}");
            foreach (var accept in ManifestListAccept)
            {
                platformRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
            }

            using var platformResponse = await http.SendAsync(platformRequest, ct);
            platformResponse.EnsureSuccessStatusCode();
            return ImageManifest.Parse(await platformResponse.Content.ReadAsStringAsync(ct));
        }

        return ImageManifest.Parse(rootJson);
    }

    private static string? SelectWindowsAmd64(JsonElement root)
    {
        if (!root.TryGetProperty("manifests", out var manifests) || manifests.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in manifests.EnumerateArray())
        {
            if (!entry.TryGetProperty("platform", out var platform))
            {
                continue;
            }

            var os = platform.TryGetProperty("os", out var o) ? o.GetString() : null;
            var arch = platform.TryGetProperty("architecture", out var a) ? a.GetString() : null;
            if (string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase)
                && string.Equals(arch, "amd64", StringComparison.OrdinalIgnoreCase)
                && entry.TryGetProperty("digest", out var d))
            {
                return d.GetString();
            }
        }

        return null;
    }

    private static async Task DownloadAsync(
        HttpClient http,
        ImageReference image,
        string digest,
        string path,
        IProgress<ContainerImageImportProgress>? progress,
        CancellationToken ct)
    {
        var url = $"https://{image.Registry}/v2/{image.Repository}/blobs/{digest}";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(path);

        var buffer = new byte[1 << 20];
        long copied = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
            progress?.Report(new ContainerImageImportProgress(
                "Downloading the base layer",
                total is > 0 ? (double)copied / total.Value : null));
        }
    }

    private static void ExtractWithTar(string tarball, string destination)
    {
        var startInfo = new ProcessStartInfo("tar.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-xf");
        startInfo.ArgumentList.Add(tarball);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(destination);

        using var process = Process.Start(startInfo)
            ?? throw new SandboxBackendException("Failed to start tar.exe to extract the image layer.", 0);

        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new SandboxBackendException(
                $"Extracting the image layer failed (tar.exe exited with {process.ExitCode}).", 0);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>A parsed <c>registry/repository:tag</c> reference.</summary>
    internal sealed record ImageReference(string Registry, string Repository, string Tag)
    {
        public static ImageReference Parse(string reference)
        {
            var value = reference.Trim();
            var slash = value.IndexOf('/');
            var registry = slash > 0 && value[..slash].Contains('.') ? value[..slash] : "mcr.microsoft.com";
            var remainder = registry == "mcr.microsoft.com" && slash < 0
                ? value
                : value[(registry.Length + 1)..];

            var colon = remainder.LastIndexOf(':');
            var repository = colon > 0 ? remainder[..colon] : remainder;
            var tag = colon > 0 ? remainder[(colon + 1)..] : "latest";
            return new ImageReference(registry, repository, tag);
        }

        public override string ToString() => $"{Registry}/{Repository}:{Tag}";
    }

    /// <summary>The parts of an image manifest the importer needs.</summary>
    internal sealed record ImageManifest(IReadOnlyList<ManifestLayer> Layers)
    {
        public static ImageManifest Parse(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var layers = new List<ManifestLayer>();
            if (doc.RootElement.TryGetProperty("layers", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var layer in arr.EnumerateArray())
                {
                    var digest = layer.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    var size = layer.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    if (digest is not null)
                    {
                        layers.Add(new ManifestLayer(digest, size));
                    }
                }
            }

            return new ImageManifest(layers);
        }
    }

    internal sealed record ManifestLayer(string Digest, long Size);
}
