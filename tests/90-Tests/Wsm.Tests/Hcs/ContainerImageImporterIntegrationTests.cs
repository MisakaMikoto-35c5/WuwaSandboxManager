using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Domain.Exceptions;
using Wsm.Hcs.Storage;
using Wsm.Hcs.Storage.Internal;
using Xunit;

namespace Wsm.Tests.Hcs;

public sealed class ContainerImageImporterIntegrationTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly byte[] KernelBytes = File.ReadAllBytes(
        Path.Combine(Environment.SystemDirectory, "notepad.exe"));

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wsm-import-" + Guid.NewGuid().ToString("N"));

    public ContainerImageImporterIntegrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task Import_finalizes_a_prepared_layer_without_leaving_work_files()
    {
        var destination = Path.Combine(_root, "image");
        var importer = CreateImporter(new FakeWcowLayerService(), CreateLayerArchive("new"));

        var result = await importer.ImportBaseLayerAsync(
            "mcr.microsoft.com/windows/servercore:test", destination).WaitAsync(Timeout);

        Assert.Equal(destination, result);
        Assert.Equal("new", File.ReadAllText(Path.Combine(result, "Files", "Windows", "marker.txt")));
        Assert.Equal(ContainerImageInstallState.Installed, ContainerImagePaths.GetInstallState(result));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Cancellation_after_process_base_image_cleans_only_this_imports_work()
    {
        var destination = Path.Combine(_root, "image");
        using var cts = new CancellationTokenSource();
        var layers = new FakeWcowLayerService { AfterProcessBaseImage = _ => cts.Cancel() };
        var importer = CreateImporter(layers, CreateLayerArchive("cancelled"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            importer.ImportBaseLayerAsync(
                "mcr.microsoft.com/windows/servercore:test", destination, ct: cts.Token).WaitAsync(Timeout));

        Assert.False(Directory.Exists(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Foreign_content_written_during_download_is_preserved_and_blocks_finalize()
    {
        var destination = Path.Combine(_root, "image");
        var blobStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlob = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var importer = CreateImporter(
            new FakeWcowLayerService(), CreateLayerArchive("import"), blobStarted, releaseBlob.Task);

        var import = importer.ImportBaseLayerAsync(
            "mcr.microsoft.com/windows/servercore:test", destination);
        await blobStarted.Task.WaitAsync(Timeout);
        Directory.CreateDirectory(destination);
        var foreignFile = Path.Combine(destination, "foreign.txt");
        File.WriteAllText(foreignFile, "keep me");
        releaseBlob.SetResult();

        await Assert.ThrowsAsync<SandboxValidationException>(() => import.WaitAsync(Timeout));

        Assert.Equal("keep me", File.ReadAllText(foreignFile));
        Assert.False(File.Exists(Path.Combine(destination, "Files", "Windows", "marker.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Concurrent_imports_use_independent_work_and_never_delete_the_winner()
    {
        var destination = Path.Combine(_root, "image");
        var firstBlobStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBlobStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlobs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = CreateImporter(
            new FakeWcowLayerService(), CreateLayerArchive("first"), firstBlobStarted, releaseBlobs.Task);
        var second = CreateImporter(
            new FakeWcowLayerService(), CreateLayerArchive("second"), secondBlobStarted, releaseBlobs.Task);

        var firstImport = first.ImportBaseLayerAsync(
            "mcr.microsoft.com/windows/servercore:test", destination);
        var secondImport = second.ImportBaseLayerAsync(
            "mcr.microsoft.com/windows/servercore:test", destination);
        await Task.WhenAll(firstBlobStarted.Task, secondBlobStarted.Task).WaitAsync(Timeout);
        releaseBlobs.SetResult();

        var outcomes = await Task.WhenAll(Capture(firstImport), Capture(secondImport)).WaitAsync(Timeout);

        Assert.Single(outcomes, outcome => outcome.Error is null);
        Assert.Single(outcomes, outcome => outcome.Error is SandboxValidationException);
        Assert.Equal(ContainerImageInstallState.Installed, ContainerImagePaths.GetInstallState(destination));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    private static ContainerImageImporter CreateImporter(
        FakeWcowLayerService layers,
        byte[] archive,
        TaskCompletionSource? blobStarted = null,
        Task? blobRelease = null)
        => new(
            layers,
            NullLogger<ContainerImageImporter>.Instance,
            new FakeRegistryHandler(archive, blobStarted, blobRelease));

    private static byte[] CreateLayerArchive(string marker)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            WriteEntry(tar, "Files/Windows/System32/ntoskrnl.exe", KernelBytes);
            WriteEntry(tar, "Files/Windows/marker.txt", Encoding.UTF8.GetBytes(marker));
        }

        return output.ToArray();
    }

    private static void WriteEntry(TarWriter tar, string name, byte[] bytes)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes),
        };
        tar.WriteEntry(entry);
    }

    private static async Task<ImportOutcome> Capture(Task<string> import)
    {
        try
        {
            return new ImportOutcome(await import, null);
        }
        catch (Exception ex)
        {
            return new ImportOutcome(null, ex);
        }
    }

    private sealed record ImportOutcome(string? Path, Exception? Error);

    private sealed class FakeRegistryHandler(
        byte[] archive,
        TaskCompletionSource? blobStarted,
        Task? blobRelease) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/manifests/", StringComparison.Ordinal))
            {
                const string digest = "sha256:0123456789abcdef";
                var json = $$"""
                    {
                      "schemaVersion": 2,
                      "mediaType": "application/vnd.docker.distribution.manifest.v2+json",
                      "config": {
                        "mediaType": "application/vnd.docker.container.image.v1+json",
                        "size": 2,
                        "digest": "sha256:fedcba9876543210"
                      },
                      "layers": [{
                        "mediaType": "application/vnd.docker.image.rootfs.diff.tar.gzip",
                        "size": {{archive.Length}},
                        "digest": "{{digest}}"
                      }]
                    }
                    """;
                return Response(request, new StringContent(json, Encoding.UTF8, "application/json"));
            }

            if (request.RequestUri.AbsolutePath.Contains("/blobs/", StringComparison.Ordinal))
            {
                blobStarted?.TrySetResult();
                if (blobRelease is not null)
                {
                    await blobRelease.WaitAsync(cancellationToken);
                }

                return Response(request, new ByteArrayContent(archive));
            }

            return Response(request, new StringContent("not found"), HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Response(
            HttpRequestMessage request,
            HttpContent content,
            HttpStatusCode status = HttpStatusCode.OK)
            => new(status) { RequestMessage = request, Content = content };
    }

    private sealed class FakeWcowLayerService : IWcowLayerService
    {
        public Action<string>? AfterProcessBaseImage { get; init; }

        public Guid GetLayerId(string layerPath) => throw new NotSupportedException();
        public void CreateScratchLayer(string scratchPath, IReadOnlyList<string> parentLayerPaths)
            => throw new NotSupportedException();
        public void Activate(string layerPath) => throw new NotSupportedException();
        public void Deactivate(string layerPath) => throw new NotSupportedException();
        public void Prepare(string layerPath, IReadOnlyList<string> parentLayerPaths)
            => throw new NotSupportedException();
        public void Unprepare(string layerPath) => throw new NotSupportedException();
        public string GetMountPath(string layerPath) => throw new NotSupportedException();
        public void Destroy(string layerPath) => throw new NotSupportedException();
        public void ImportLayer(string layerPath, string importFolderPath, IReadOnlyList<string> parentLayerPaths)
            => throw new NotSupportedException();

        public void ProcessBaseImage(string layerPath)
        {
            Directory.CreateDirectory(Path.Combine(layerPath, "Hives"));
            AfterProcessBaseImage?.Invoke(layerPath);
        }
    }
}
