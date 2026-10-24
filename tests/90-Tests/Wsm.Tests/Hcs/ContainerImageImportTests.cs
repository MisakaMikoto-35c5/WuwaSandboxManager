using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Hcs.Storage;
using Wsm.Hcs.Storage.Internal;
using Xunit;

namespace Wsm.Tests.Hcs;

/// <summary>
/// Covers the suggested-image catalog, the shared default layer path/slug, the
/// import progress record (including its legacy English stage text) and the
/// install-state probe the GUI uses to detect an already-downloaded image.
/// </summary>
public sealed class ContainerImageImportTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wsm-image-" + Guid.NewGuid().ToString("N"));

    public ContainerImageImportTests() => Directory.CreateDirectory(_root);

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
    public void The_catalog_offers_four_images_including_both_full_server_images()
    {
        var catalog = ContainerImageImporter.Catalog;

        Assert.Equal(4, catalog.Count);
        var references = catalog.Select(image => image.Reference).ToArray();
        Assert.Contains("mcr.microsoft.com/windows/server:ltsc2025", references);
        Assert.Contains("mcr.microsoft.com/windows/server:ltsc2022", references);
        Assert.Contains("mcr.microsoft.com/windows/servercore:ltsc2025", references);
        Assert.Contains("mcr.microsoft.com/windows/servercore:ltsc2022", references);
        Assert.All(catalog, image =>
        {
            Assert.False(string.IsNullOrWhiteSpace(image.Id));
            Assert.False(string.IsNullOrWhiteSpace(image.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(image.DescriptionKey));
            Assert.True(image.ApproxSizeGB > 0);
        });
    }

    [Fact]
    public void Suggested_images_stay_in_sync_with_the_catalog()
    {
        var importer = new ContainerImageImporter(null!, NullLogger<ContainerImageImporter>.Instance);

        Assert.Equal(
            ContainerImageImporter.Catalog.Select(image => image.Reference),
            importer.SuggestedImages);
        Assert.Same(ContainerImageImporter.Catalog, importer.ImageCatalog);
    }

    [Fact]
    public void The_full_server_images_are_recognized_as_server_images()
    {
        Assert.True(ContainerImageImporter.IsServerRepository("windows/server"));
        Assert.True(ContainerImageImporter.IsServerRepository("windows/servercore"));
    }

    [Theory]
    [InlineData(null, "image")]
    [InlineData("", "image")]
    [InlineData("   ", "image")]
    [InlineData("mcr.microsoft.com/windows/server:ltsc2025", "mcr.microsoft.com-windows-server-ltsc2025")]
    [InlineData("windows/servercore:ltsc2022", "windows-servercore-ltsc2022")]
    public void The_slug_turns_a_reference_into_a_single_folder_name(string? reference, string expected)
        => Assert.Equal(expected, ContainerImagePaths.Slugify(reference));

    [Fact]
    public void The_default_layer_path_lives_under_the_shared_images_root()
    {
        var path = ContainerImagePaths.GetDefaultLayerPath("mcr.microsoft.com/windows/server:ltsc2025");

        Assert.Equal(ContainerImagePaths.GetDefaultRoot(), Path.GetDirectoryName(path));
        Assert.Equal("mcr.microsoft.com-windows-server-ltsc2025", Path.GetFileName(path));
        Assert.EndsWith(
            Path.Combine("WindowsSandboxManager", "images", "mcr.microsoft.com-windows-server-ltsc2025"),
            path);
    }

    [Fact]
    public void Progress_keeps_the_legacy_english_stage_text()
    {
        Assert.Equal("Resolving the image manifest",
            new ContainerImageImportProgress(ContainerImageImportStage.ResolvingManifest).Message);
        Assert.Equal("Downloading the base layer",
            new ContainerImageImportProgress(ContainerImageImportStage.Downloading).Message);
        Assert.Equal("Extracting the layer",
            new ContainerImageImportProgress(ContainerImageImportStage.Extracting).Message);
        Assert.Equal("Preparing the layer",
            new ContainerImageImportProgress(ContainerImageImportStage.Preparing).Message);
        Assert.Equal("Done",
            new ContainerImageImportProgress(ContainerImageImportStage.Done).Message);
    }

    [Fact]
    public void Progress_carries_optional_fraction_and_byte_counters()
    {
        var bare = new ContainerImageImportProgress(ContainerImageImportStage.Extracting);
        Assert.Null(bare.Fraction);
        Assert.Null(bare.BytesReceived);
        Assert.Null(bare.TotalBytes);

        var downloading = new ContainerImageImportProgress(
            ContainerImageImportStage.Downloading, 0.5, BytesReceived: 100, TotalBytes: 200);
        Assert.Equal(0.5, downloading.Fraction);
        Assert.Equal(100, downloading.BytesReceived);
        Assert.Equal(200, downloading.TotalBytes);
    }

    [Fact]
    public void The_install_state_is_missing_for_absent_or_empty_folders()
    {
        Assert.Equal(ContainerImageInstallState.Missing, ContainerImagePaths.GetInstallState(null));
        Assert.Equal(ContainerImageInstallState.Missing, ContainerImagePaths.GetInstallState("  "));
        Assert.Equal(
            ContainerImageInstallState.Missing,
            ContainerImagePaths.GetInstallState(Path.Combine(_root, "does-not-exist")));

        var empty = Directory.CreateDirectory(Path.Combine(_root, "empty"));
        Assert.Equal(ContainerImageInstallState.Missing, ContainerImagePaths.GetInstallState(empty.FullName));
    }

    [Fact]
    public void The_install_state_is_installed_when_the_metadata_file_exists()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "installed"));
        File.WriteAllText(Path.Combine(folder.FullName, ContainerImageMetadata.FileName), "{}");

        Assert.Equal(ContainerImageInstallState.Installed, ContainerImagePaths.GetInstallState(folder.FullName));
    }

    [Fact]
    public void The_install_state_is_occupied_for_foreign_content()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "occupied"));
        File.WriteAllText(Path.Combine(folder.FullName, "readme.txt"), "not an image");

        Assert.Equal(ContainerImageInstallState.Occupied, ContainerImagePaths.GetInstallState(folder.FullName));
    }
}
