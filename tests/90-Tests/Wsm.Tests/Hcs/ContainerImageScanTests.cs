using Wsm.Hcs.Storage;
using Xunit;

namespace Wsm.Tests.Hcs;

/// <summary>
/// Covers the imported-image scan the isolation policy consults: which folders
/// count as an image, the server-first / newest-build-first ordering, and the
/// folder-name SKU inference that upgrades hand-imported server images whose
/// build came from the kernel fallback.
/// </summary>
public sealed class ContainerImageScanTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wsm-scan-" + Guid.NewGuid().ToString("N"));

    public ContainerImageScanTests() => Directory.CreateDirectory(_root);

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
    public void The_legacy_import_root_is_the_documented_manual_import_folder()
        => Assert.Contains(@"C:\wsm-images", ContainerImagePaths.LegacyImportRoots);

    [Fact]
    public void Only_folders_with_a_files_windows_tree_and_readable_metadata_count()
    {
        var good = CreateLayer("good", build: 20348, isServer: true);

        // A layer tree without metadata and without a kernel to read is skipped.
        Directory.CreateDirectory(Path.Combine(_root, "no-metadata", "Files", "Windows"));

        // A half-imported layer (no ProcessBaseImage 'Hives' links) cannot back
        // a scratch layer — CreateSandboxLayer answers 0x80070003 for it.
        var noHives = Path.Combine(_root, "no-hives");
        Directory.CreateDirectory(Path.Combine(noHives, "Files", "Windows"));
        new ContainerImageMetadata { Build = 20348, IsServer = true, SourceReference = "test" }.Write(noHives);

        // Metadata alone does not make a folder an image.
        var noWindows = Path.Combine(_root, "no-windows");
        Directory.CreateDirectory(noWindows);
        new ContainerImageMetadata { Build = 20348, IsServer = true, SourceReference = "test" }.Write(noWindows);

        // Files in the root are not folders.
        File.WriteAllText(Path.Combine(_root, "a-file.txt"), "x");

        var candidates = ContainerImagePaths.FindImportedImages([_root]);

        var candidate = Assert.Single(candidates);
        Assert.Equal(good, candidate.Path);
        Assert.Equal(20348, candidate.Metadata.Build);
        Assert.True(candidate.Metadata.IsServer);
    }

    [Fact]
    public void Candidates_are_ordered_server_first_then_newest_build_first()
    {
        CreateLayer("client-26300", build: 26300, isServer: false);
        CreateLayer("server-20348", build: 20348, isServer: true);
        CreateLayer("server-26100", build: 26100, isServer: true);

        var candidates = ContainerImagePaths.FindImportedImages([_root]);

        Assert.Equal(
            new[] { "server-26100", "server-20348", "client-26300" },
            candidates.Select(candidate => Path.GetFileName(candidate.Path)).ToArray());
    }

    [Fact]
    public void A_missing_root_is_skipped()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var candidates = ContainerImagePaths.FindImportedImages([missing, _root]);

        Assert.Empty(candidates);
    }

    [Theory]
    [InlineData("servercore-ltsc2022", true)]
    [InlineData("server-full-ltsc2022", true)]
    [InlineData("mcr.microsoft.com-windows-server-ltsc2025", true)]
    [InlineData("nanoserver-ltsc2025", true)]
    [InlineData("windows/server", true)]
    [InlineData("windows-ltsc2022", false)]
    [InlineData("sandbox-base", false)]
    public void Server_skus_are_recognized_from_a_repository_or_folder_name(string name, bool expected)
        => Assert.Equal(expected, ContainerImagePaths.IsServerImageName(name));

    [Fact]
    public void The_kernel_fallback_sku_is_inferred_from_the_folder_name_and_written_back()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "servercore-ltsc2022")).FullName;
        var fallback = new ContainerImageMetadata { Build = 20348, IsServer = false };

        var enriched = ContainerImagePaths.EnrichMetadataFromFolderName(folder, fallback);

        Assert.True(enriched.IsServer);
        var written = ContainerImageMetadata.TryRead(folder);
        Assert.NotNull(written);
        Assert.True(written!.IsServer);
    }

    [Fact]
    public void Existing_metadata_is_never_rewritten_from_the_folder_name()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "servercore-ltsc2022")).FullName;
        var original = new ContainerImageMetadata { Build = 20348, IsServer = false, SourceReference = "test" };
        original.Write(folder);

        var result = ContainerImagePaths.EnrichMetadataFromFolderName(folder, original);

        Assert.False(result.IsServer);
        Assert.False(ContainerImageMetadata.TryRead(folder)!.IsServer);
    }

    private string CreateLayer(string name, int build, bool isServer)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(path, "Files", "Windows"));
        Directory.CreateDirectory(Path.Combine(path, "Hives"));
        new ContainerImageMetadata { Build = build, IsServer = isServer, SourceReference = "test" }.Write(path);
        return path;
    }
}
