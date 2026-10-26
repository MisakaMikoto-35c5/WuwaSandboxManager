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
    /// The Windows Server images offered for process isolation. All are exempt
    /// from HCS's host/image build-match rule on a Windows 11 host. Sizes are
    /// informational only. Note: the importer downloads the base OS layer only
    /// (see the class comment); that shortcut is proven for the Server Core
    /// images, while the full <c>windows/server</c> images share the same
    /// manifest shape and are expected to behave the same, but have not been
    /// boot-verified yet.
    /// </summary>
    internal static readonly IReadOnlyList<ContainerImageInfo> Catalog =
    [
        new ContainerImageInfo
        {
            Id = "windows-server-ltsc2025",
            DisplayName = "Windows Server 2025 (full)",
            Reference = "mcr.microsoft.com/windows/server:ltsc2025",
            ApproxSizeGB = 5.5,
            DescriptionKey = "ContainerImage.Desc.WindowsServer2025",
        },
        new ContainerImageInfo
        {
            Id = "windows-server-ltsc2022",
            DisplayName = "Windows Server 2022 (full)",
            Reference = "mcr.microsoft.com/windows/server:ltsc2022",
            ApproxSizeGB = 5.0,
            DescriptionKey = "ContainerImage.Desc.WindowsServer2022",
        },
        new ContainerImageInfo
        {
            Id = "windows-servercore-ltsc2025",
            DisplayName = "Server Core 2025",
            Reference = "mcr.microsoft.com/windows/servercore:ltsc2025",
            ApproxSizeGB = 1.5,
            DescriptionKey = "ContainerImage.Desc.ServerCore2025",
        },
        new ContainerImageInfo
        {
            Id = "windows-servercore-ltsc2022",
            DisplayName = "Server Core 2022",
            Reference = "mcr.microsoft.com/windows/servercore:ltsc2022",
            ApproxSizeGB = 1.5,
            DescriptionKey = "ContainerImage.Desc.ServerCore2022",
        },
    ];

    private static readonly string[] ManifestListAccept =
    [
        "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.index.v1+json",
        "application/vnd.docker.distribution.manifest.v2+json",
    ];

    private readonly IWcowLayerService _layers;
    private readonly ILogger<ContainerImageImporter> _logger;
    private readonly HttpMessageHandler? _httpHandler;

    public ContainerImageImporter(IWcowLayerService layers, ILogger<ContainerImageImporter> logger)
    {
        _layers = layers;
        _logger = logger;
    }

    internal ContainerImageImporter(
        IWcowLayerService layers,
        ILogger<ContainerImageImporter> logger,
        HttpMessageHandler httpHandler)
        : this(layers, logger)
    {
        _httpHandler = httpHandler;
    }

    public IReadOnlyList<string> SuggestedImages => Catalog.Select(image => image.Reference).ToArray();

    public IReadOnlyList<ContainerImageInfo> ImageCatalog => Catalog;

    public async Task<string> ImportBaseLayerAsync(
        string reference,
        string layerPath,
        IProgress<ContainerImageImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var image = ImageReference.Parse(string.IsNullOrWhiteSpace(reference) ? DefaultReference : reference);
        var destination = Path.GetFullPath(layerPath);
        EnsureDestinationIsEmpty(destination);

        var parent = Path.GetDirectoryName(destination)
            ?? throw new SandboxValidationException($"The target layer path '{destination}' has no parent directory.");
        Directory.CreateDirectory(parent);
        var work = destination.TrimEnd('\\', '/') + ".wsm-" + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(work);

        try
        {
            await ImportCoreAsync(image, work, progress, ct);
            ct.ThrowIfCancellationRequested();
            FinalizeImport(work, destination, ct);
        }
        catch
        {
            TryDeleteDirectory(work);
            throw;
        }

        _logger.LogInformation("Base layer for {Image} is ready at '{Destination}'.", image, destination);
        progress?.Report(new ContainerImageImportProgress(ContainerImageImportStage.Done, 1));
        return destination;
    }

    private static void EnsureDestinationIsEmpty(string destination)
    {
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new SandboxValidationException(
                $"The target layer directory '{destination}' is not empty; choose a new path.");
        }
    }

    private static void FinalizeImport(string work, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            EnsureDestinationIsEmpty(destination);
            if (Directory.Exists(destination))
            {
                try
                {
                    Directory.Delete(destination, recursive: false);
                }
                catch (DirectoryNotFoundException)
                {
                    // Another finalizer removed the still-empty directory.
                }
            }

            ct.ThrowIfCancellationRequested();
            Directory.Move(work, destination);
        }
        catch (IOException) when (Directory.Exists(destination)
                                  && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new SandboxValidationException(
                $"The target layer directory '{destination}' is not empty; choose a new path.");
        }
    }

    private async Task ImportCoreAsync(
        ImageReference image,
        string destination,
        IProgress<ContainerImageImportProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var http = new HttpClient(_httpHandler ?? new HttpClientHandler(), disposeHandler: _httpHandler is null)
        {
            Timeout = TimeSpan.FromMinutes(30),
        };

        // 1. Resolve the platform-specific manifest.
        progress?.Report(new ContainerImageImportProgress(ContainerImageImportStage.ResolvingManifest));
        var manifest = await GetManifestAsync(http, image, ct);
        var (digest, size) = manifest.Layers.Count > 0
            ? (manifest.Layers[0].Digest, manifest.Layers[0].Size)
            : throw new SandboxBackendException($"Image '{image}' has no layers to import.", 0);

        _logger.LogInformation(
            "Importing base layer {Digest} ({SizeMb} MB) of {Image} into '{Destination}'.",
            digest, size / (1024 * 1024), image, destination);

        // 2. Download the base layer blob into this import's unique work directory.
        var tarball = Path.Combine(destination, Guid.NewGuid().ToString("N") + ".layer.tgz");
        try
        {
            await DownloadAsync(http, image, digest, tarball, progress, ct);

            // 3. Extract (Files/, UtilityVM/, ...).
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ContainerImageImportProgress(ContainerImageImportStage.Extracting));
            await ExtractWithTarAsync(tarball, destination, ct);

            // 4. Prepare it as a WCOW base layer (Hives links + blank VHDX).
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ContainerImageImportProgress(ContainerImageImportStage.Preparing));
            ct.ThrowIfCancellationRequested();
            _layers.ProcessBaseImage(destination);
            ct.ThrowIfCancellationRequested();

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
    }

    /// <summary>Server images are the ones HCS exempts from the build-match rule.</summary>
    internal static bool IsServerRepository(string repository)
        => ContainerImagePaths.IsServerImageName(repository);

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
                ContainerImageImportStage.Downloading,
                total is > 0 ? (double)copied / total.Value : null,
                BytesReceived: copied,
                TotalBytes: total is > 0 ? total.Value : null));
        }
    }

    private static async Task ExtractWithTarAsync(string tarball, string destination, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo("tar.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-xf");
        startInfo.ArgumentList.Add(tarball);
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(destination);

        using var process = Process.Start(startInfo)
            ?? throw new SandboxBackendException("Failed to start tar.exe to extract the image layer.", 0);

        // Read both pipes asynchronously so a chatty tar cannot deadlock us, and
        // keep its diagnosis: a bare "tar.exe exited with 1" is unusable when a
        // layer contains an entry Windows will not create (long paths, reserved
        // names, ...).
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort; the process may have exited on its own.
            }

            throw;
        }

        var details = ((await stderr) + Environment.NewLine + (await stdout)).Trim();

        if (process.ExitCode != 0)
        {
            throw new SandboxBackendException(
                $"Extracting the image layer failed (tar.exe exited with {process.ExitCode})."
                + (details.Length == 0 ? string.Empty : $" tar said: {Truncate(details, 4000)}"),
                0);
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + " ...";

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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort; this path belongs only to the failed import.
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
