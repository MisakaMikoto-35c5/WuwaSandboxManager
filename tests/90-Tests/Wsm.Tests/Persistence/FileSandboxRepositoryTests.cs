using Microsoft.Extensions.Options;
using Wsm.Domain;
using Wsm.Domain.ValueObjects;
using Wsm.Persistence;
using Wsm.Persistence.Internal;
using Xunit;

namespace Wsm.Tests.Persistence;

/// <summary>
/// Covers the portable <c>.wsm</c> path rules: the user-data folder and each
/// mapped host folder are stored relative to the <c>.wsm</c> when they lie
/// inside the sandbox folder, and absolute when they lie outside; on load a
/// relative path resolves against the <c>.wsm</c> folder and an absolute path is
/// used as-is. Missing paths are tolerated.
/// </summary>
public sealed class FileSandboxRepositoryTests : IDisposable
{
    private readonly string _configDataFolder =
        Path.Combine(Path.GetTempPath(), "wsm-repo-" + Guid.NewGuid().ToString("N"));

    private readonly FileSandboxRepository _repository;

    public FileSandboxRepositoryTests()
        => _repository = new FileSandboxRepository(
            Options.Create(new SandboxPersistenceOptions { ConfigDataFolder = _configDataFolder }));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_configDataFolder))
            {
                Directory.Delete(_configDataFolder, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private string SandboxFolder(SandboxInstanceId id)
        => Path.Combine(_configDataFolder, id.Value.ToString("D"));

    private MappedFolder StoredMappedFolder(SandboxInstanceId id)
        => Assert.Single(SandboxManifest.TryReadFile(_repository.GetManifestPath(id))!.Recipe.MappedFolders);

    [Fact]
    public async Task Mapped_folder_inside_the_sandbox_folder_is_stored_relative()
    {
        var id = SandboxInstanceId.New();
        var inside = Path.Combine(SandboxFolder(id), "shared");
        var recipe = SandboxRecipe.Default.WithMappedFolder(new MappedFolder(inside, readOnly: false));
        var instance = SandboxInstance.Create(new SandboxName("Inside"), recipe, id);

        await _repository.SaveAsync(instance);

        // Written into the .wsm as a relative path (no drive/root).
        var stored = StoredMappedFolder(id);
        Assert.Equal("shared", stored.HostFolder);
        Assert.False(Path.IsPathFullyQualified(stored.HostFolder));

        // Resolved back against the .wsm folder on load, preserving access mode.
        var loaded = await _repository.GetAsync(id);
        var folder = Assert.Single(loaded!.Recipe.MappedFolders);
        Assert.Equal(inside, folder.HostFolder);
        Assert.False(folder.ReadOnly);
    }

    [Fact]
    public async Task Mapped_folder_outside_the_sandbox_folder_is_stored_absolute()
    {
        var id = SandboxInstanceId.New();
        var outside = Path.Combine(Path.GetTempPath(), "wsm-external-" + Guid.NewGuid().ToString("N"));
        var recipe = SandboxRecipe.Default.WithMappedFolder(new MappedFolder(outside, readOnly: true));
        var instance = SandboxInstance.Create(new SandboxName("Outside"), recipe, id);

        await _repository.SaveAsync(instance);

        // Written into the .wsm unchanged, absolute (never assumed to be relative).
        Assert.Equal(outside, StoredMappedFolder(id).HostFolder);

        var loaded = await _repository.GetAsync(id);
        Assert.Equal(outside, Assert.Single(loaded!.Recipe.MappedFolders).HostFolder);
    }

    [Fact]
    public async Task Missing_mapped_path_is_tolerated()
    {
        var id = SandboxInstanceId.New();
        var missing = @"Z:\definitely\not\present";
        var recipe = SandboxRecipe.Default.WithMappedFolder(new MappedFolder(missing, readOnly: true));
        var instance = SandboxInstance.Create(new SandboxName("Missing"), recipe, id);

        await _repository.SaveAsync(instance);

        // No existence check: a mapped path that is absent on this machine loads
        // as-is instead of throwing (another machine may have it).
        var loaded = await _repository.GetAsync(id);
        Assert.Equal(missing, Assert.Single(loaded!.Recipe.MappedFolders).HostFolder);
    }

    [Fact]
    public async Task Mapped_relative_path_follows_a_moved_folder()
    {
        var id = SandboxInstanceId.New();
        var inside = Path.Combine(SandboxFolder(id), "data", "share");
        var recipe = SandboxRecipe.Default.WithMappedFolder(new MappedFolder(inside, readOnly: true));
        var instance = SandboxInstance.Create(new SandboxName("Moved"), recipe, id);

        await _repository.SaveAsync(instance);
        var sourcePath = _repository.GetManifestPath(id);

        // Move the whole sandbox folder elsewhere and open the .wsm from there.
        var movedFolder = Path.Combine(Path.GetTempPath(), "wsm-moved-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(movedFolder);
        try
        {
            var movedPath = Path.Combine(movedFolder, Path.GetFileName(sourcePath));
            File.Copy(sourcePath, movedPath);

            var imported = await _repository.ImportAsync(movedPath);

            Assert.Equal(
                Path.Combine(movedFolder, "data", "share"),
                Assert.Single(imported.Recipe.MappedFolders).HostFolder);
        }
        finally
        {
            Directory.Delete(movedFolder, recursive: true);
        }
    }
}
