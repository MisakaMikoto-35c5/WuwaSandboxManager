using Microsoft.Extensions.Options;
using Wsm.Domain;
using Wsm.Domain.ValueObjects;
using Wsm.Persistence;
using Wsm.Persistence.Internal;
using Xunit;

namespace Wsm.Tests.Persistence;

/// <summary>
/// Covers the on-disk layout (a sandbox lives at
/// <c>&lt;configDataFolder&gt;\&lt;Name&gt;\&lt;Name&gt;.wsm</c> with its data
/// disks beside it), name sanitization and collision handling, id-based lookup
/// (including the scan fallback), rename-relocation and delete, plus the
/// portable <c>.wsm</c> path rules: the user-data folder and each mapped host
/// folder are stored relative to the <c>.wsm</c> when they lie inside the
/// sandbox folder, and absolute when they lie outside; on load a relative path
/// resolves against the <c>.wsm</c> folder and an absolute path is used as-is.
/// Missing paths are tolerated.
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

    private FileSandboxRepository NewRepository()
        => new(Options.Create(new SandboxPersistenceOptions { ConfigDataFolder = _configDataFolder }));

    private string SandboxFolder(string name) => Path.Combine(_configDataFolder, name);

    private string SandboxManifestPath(string name)
        => Path.Combine(SandboxFolder(name), name + SandboxManifest.FileExtension);

    private MappedFolder StoredMappedFolder(SandboxInstanceId id)
        => Assert.Single(SandboxManifest.TryReadFile(_repository.GetManifestPath(id))!.Recipe.MappedFolders);

    [Fact]
    public async Task Sandbox_folder_and_manifest_are_named_after_the_sandbox_name()
    {
        var id = SandboxInstanceId.New();
        var instance = SandboxInstance.Create(new SandboxName("My Sandbox"), id: id);

        await _repository.SaveAsync(instance);

        var expected = SandboxManifestPath("My Sandbox");
        Assert.Equal(expected, _repository.GetManifestPath(id));
        Assert.True(File.Exists(expected));
        Assert.Equal(id.Value, SandboxManifest.TryReadFile(expected)?.Id);
    }

    [Theory]
    [InlineData("My: Sandbox?", "My Sandbox")]
    [InlineData("  Spaced   Name  ", "Spaced Name")]
    [InlineData("Trailing dots... ", "Trailing dots")]
    [InlineData("a/b\\c", "abc")]
    public async Task Names_are_sanitized_for_the_file_system(string raw, string expected)
    {
        var id = SandboxInstanceId.New();
        var instance = SandboxInstance.Create(new SandboxName(raw), id: id);

        await _repository.SaveAsync(instance);

        Assert.Equal(SandboxManifestPath(expected), _repository.GetManifestPath(id));
        Assert.True(File.Exists(SandboxManifestPath(expected)));
    }

    [Fact]
    public async Task Name_that_sanitizes_to_nothing_falls_back_to_the_id()
    {
        var id = SandboxInstanceId.New();
        var instance = SandboxInstance.Create(new SandboxName("///"), id: id);

        await _repository.SaveAsync(instance);

        var folder = id.Value.ToString("D");
        Assert.Equal(SandboxManifestPath(folder), _repository.GetManifestPath(id));
        Assert.True(File.Exists(SandboxManifestPath(folder)));
    }

    [Fact]
    public async Task Colliding_names_get_a_numeric_suffix()
    {
        var first = SandboxInstance.Create(new SandboxName("Dup"));
        var second = SandboxInstance.Create(new SandboxName("Dup"));
        var third = SandboxInstance.Create(new SandboxName("Dup"));

        await _repository.SaveAsync(first);
        await _repository.SaveAsync(second);
        await _repository.SaveAsync(third);

        Assert.Equal(SandboxManifestPath("Dup"), _repository.GetManifestPath(first.Id));
        Assert.Equal(SandboxManifestPath("Dup (2)"), _repository.GetManifestPath(second.Id));
        Assert.Equal(SandboxManifestPath("Dup (3)"), _repository.GetManifestPath(third.Id));
        Assert.True(File.Exists(SandboxManifestPath("Dup")));
        Assert.True(File.Exists(SandboxManifestPath("Dup (2)")));
        Assert.True(File.Exists(SandboxManifestPath("Dup (3)")));
    }

    [Fact]
    public async Task Collision_is_avoided_across_repository_instances()
    {
        var first = SandboxInstance.Create(new SandboxName("Shared"));
        await _repository.SaveAsync(first);

        // A fresh repository (empty in-memory index) must still not clobber the
        // existing folder: it discovers the owner by scanning the manifest.
        var other = NewRepository();
        var second = SandboxInstance.Create(new SandboxName("Shared"));
        await other.SaveAsync(second);

        Assert.Equal(SandboxManifestPath("Shared (2)"), other.GetManifestPath(second.Id));
        Assert.True(File.Exists(SandboxManifestPath("Shared")));
        Assert.True(File.Exists(SandboxManifestPath("Shared (2)")));
    }

    [Fact]
    public async Task Resolving_by_id_works_from_a_fresh_repository_instance()
    {
        var id = SandboxInstanceId.New();
        var instance = SandboxInstance.Create(new SandboxName("Fresh"), id: id);
        await _repository.SaveAsync(instance);
        var savedPath = _repository.GetManifestPath(id);

        // A brand-new repository has no in-memory index, so it must find the
        // manifest by scanning for the matching id (the id is not in the path).
        var fresh = NewRepository();
        Assert.Equal(savedPath, fresh.GetManifestPath(id));

        var loaded = await fresh.GetAsync(id);
        Assert.NotNull(loaded);
        Assert.Equal(id, loaded!.Id);
        Assert.Equal("Fresh", loaded.Name.Value);
    }

    [Fact]
    public async Task Rename_moves_the_folder_and_manifest()
    {
        var id = SandboxInstanceId.New();
        var instance = SandboxInstance.Create(new SandboxName("Before"), id: id);
        await _repository.SaveAsync(instance);

        // A data disk beside the manifest must travel with the folder.
        var dataDisk = Path.Combine(SandboxFolder("Before"), "sandbox.vhdx");
        await File.WriteAllTextAsync(dataDisk, "data");

        instance.Rename(new SandboxName("After"));
        await _repository.SaveAsync(instance);

        var newManifest = SandboxManifestPath("After");
        Assert.False(Directory.Exists(SandboxFolder("Before")));
        Assert.True(File.Exists(newManifest));
        Assert.True(File.Exists(Path.Combine(SandboxFolder("After"), "sandbox.vhdx")));
        Assert.Equal(newManifest, _repository.GetManifestPath(id));
        Assert.Equal(id.Value, SandboxManifest.TryReadFile(newManifest)?.Id);
        Assert.Equal("After", SandboxManifest.TryReadFile(newManifest)?.Name);
    }

    [Fact]
    public async Task Delete_removes_the_sandbox_folder()
    {
        var id = SandboxInstanceId.New();
        var instance = SandboxInstance.Create(new SandboxName("Doomed"), id: id);
        await _repository.SaveAsync(instance);
        await File.WriteAllTextAsync(Path.Combine(SandboxFolder("Doomed"), "sandbox.vhdx"), "data");

        await _repository.DeleteAsync(id);

        Assert.False(Directory.Exists(SandboxFolder("Doomed")));
    }

    [Fact]
    public async Task Mapped_folder_inside_the_sandbox_folder_is_stored_relative()
    {
        var id = SandboxInstanceId.New();
        var inside = Path.Combine(SandboxFolder("Inside"), "shared");
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
        var inside = Path.Combine(SandboxFolder("Moved"), "data", "share");
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
