using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Wsm.Domain;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;

namespace Wsm.Persistence.Internal;

/// <summary>
/// File-system backed repository where each sandbox is a single portable
/// <c>.wsm</c> file.
/// <para>
/// A sandbox created by the app lives at
/// <c>&lt;configDataFolder&gt;\&lt;Name&gt;\&lt;Name&gt;.wsm</c> with its
/// user-data disks beside it. The folder and file are named after the
/// user-set sandbox name (sanitized for the file system); when a name is
/// already owned by a different sandbox a numeric suffix is appended (for
/// example <c>Name (2)</c>). The repository also lists every <c>*.wsm</c>
/// found (recursively) under the config &amp; data folder and indexes any
/// <c>.wsm</c> opened from an arbitrary location (USB stick, another PC) for
/// the session.
/// </para>
/// <para>
/// Lookups by id do not assume the id appears in the path: the in-memory
/// index is consulted first, then the config &amp; data folder is scanned for
/// the manifest whose <c>Id</c> matches. Renaming a managed sandbox moves its
/// whole folder (manifest and data disks) to the new name-based path.
/// </para>
/// <para>
/// The user-data folder and any mapped folder that lies inside the sandbox
/// folder are stored <b>relative</b> to the <c>.wsm</c> so the folder can be
/// moved; mapped folders outside it are stored absolute. On load a relative path
/// is resolved against the <c>.wsm</c> folder and an absolute path is used
/// as-is; missing paths are tolerated (the engine reprovisions as needed).
/// </para>
/// </summary>
[ExposeServices(typeof(ISandboxInstanceRepository))]
internal sealed class FileSandboxRepository : ISandboxInstanceRepository, ISingletonDependency
{
    /// <summary>Search pattern used to discover sandbox files under the config &amp; data folder.</summary>
    public const string ManifestSearchPattern = "*.wsm";

    private readonly string _configDataFolder;

    /// <summary>Id → <c>.wsm</c> path for files opened from outside the config &amp; data folder.</summary>
    private readonly ConcurrentDictionary<SandboxInstanceId, string> _knownPaths = new();

    public FileSandboxRepository(IOptions<SandboxPersistenceOptions> options)
    {
        _configDataFolder = options.Value.ResolveConfigDataFolder();
        Directory.CreateDirectory(_configDataFolder);
    }

    public async Task SaveAsync(SandboxInstance instance, CancellationToken cancellationToken = default)
    {
        string path;

        if (_knownPaths.TryGetValue(instance.Id, out var known))
        {
            path = known;

            // A managed sandbox whose folder name no longer matches the current
            // name is relocated to the new name-based path. Files opened from an
            // arbitrary location (outside the config & data folder) are written
            // in place so importing never moves the user's folder.
            var currentFolder = Path.GetDirectoryName(known);
            if (!string.IsNullOrEmpty(currentFolder) && IsManagedFolder(currentFolder))
            {
                var desiredFolder = Path.Combine(_configDataFolder, ChooseUniqueFolderName(instance.Name.Value, instance.Id));
                if (!PathsEqual(currentFolder, desiredFolder))
                {
                    path = RelocateSandbox(known, desiredFolder);
                }
            }
        }
        else
        {
            // New sandbox: <configDataFolder>\<unique-name>\<unique-name>.wsm.
            var folderName = ChooseUniqueFolderName(instance.Name.Value, instance.Id);
            var folder = Path.Combine(_configDataFolder, folderName);
            path = Path.Combine(folder, folderName + SandboxManifest.FileExtension);
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var manifest = ToPortable(instance, path);
        await File.WriteAllTextAsync(path, manifest.ToJson(), cancellationToken).ConfigureAwait(false);
        _knownPaths[instance.Id] = path;
    }

    public async Task<SandboxInstance?> GetAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(id);
        var manifest = await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
        return manifest is null ? null : FromPortable(manifest, path);
    }

    public async Task<IReadOnlyList<SandboxInstance>> ListAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<SandboxInstance>();
        var seen = new HashSet<SandboxInstanceId>();

        foreach (var path in EnumerateManifestPaths())
        {
            var manifest = await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            var id = new SandboxInstanceId(manifest.Id);
            if (!seen.Add(id))
            {
                continue;
            }

            _knownPaths[id] = path;
            results.Add(FromPortable(manifest, path));
        }

        return results.OrderBy(i => i.CreatedUtc).ToArray();
    }

    public Task DeleteAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(id);
        _knownPaths.TryRemove(id, out _);
        DeleteSandbox(path, id);
        return Task.CompletedTask;
    }

    public string GetManifestPath(SandboxInstanceId id) => ResolvePath(id);

    public async Task<SandboxInstance> ImportAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            throw new SandboxValidationException("A .wsm path is required.");
        }

        var path = Path.GetFullPath(manifestPath.Trim().Trim('"'));
        var manifest = await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new SandboxValidationException($"'{manifestPath}' is not a valid .wsm sandbox file.");

        var instance = FromPortable(manifest, path);
        _knownPaths[instance.Id] = path;
        return instance;
    }

    // --------------------------- Paths ---------------------------

    /// <summary>
    /// Resolves the <c>.wsm</c> path for an id. The in-memory index is the
    /// primary lookup; an unknown id is resolved by scanning the config &amp;
    /// data folder for the manifest whose <c>Id</c> matches (the id is never
    /// assumed to appear in the path). When nothing is found a deterministic
    /// id-named fallback path is returned (it may not exist yet).
    /// </summary>
    private string ResolvePath(SandboxInstanceId id)
    {
        if (_knownPaths.TryGetValue(id, out var known))
        {
            return known;
        }

        var discovered = FindManifestPathById(id);
        if (discovered is not null)
        {
            _knownPaths[id] = discovered;
            return discovered;
        }

        return Path.Combine(
            _configDataFolder,
            id.Value.ToString("D"),
            $"{id.Value:D}{SandboxManifest.FileExtension}");
    }

    /// <summary>Finds the <c>.wsm</c> under the config &amp; data folder whose manifest id matches.</summary>
    private string? FindManifestPathById(SandboxInstanceId id)
    {
        foreach (var path in EnumerateManifestPaths())
        {
            var manifest = SandboxManifest.TryReadFile(path);
            if (manifest is not null && manifest.Id == id.Value)
            {
                return path;
            }
        }

        return null;
    }

    private IEnumerable<string> EnumerateManifestPaths()
    {
        var paths = new List<string>();

        try
        {
            if (Directory.Exists(_configDataFolder))
            {
                paths.AddRange(Directory.EnumerateFiles(
                    _configDataFolder, ManifestSearchPattern, SearchOption.AllDirectories));
            }
        }
        catch
        {
            // Inaccessible config folder: fall back to the opened files only.
        }

        foreach (var known in _knownPaths.Values)
        {
            if (File.Exists(known) && !paths.Contains(known, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(known);
            }
        }

        return paths;
    }

    /// <summary>
    /// Picks a folder name for <paramref name="rawName"/> that no other sandbox
    /// owns. The sanitized name is used as-is when free; otherwise a numeric
    /// suffix is appended (<c>Name (2)</c>, <c>Name (3)</c>, …).
    /// </summary>
    private string ChooseUniqueFolderName(string rawName, SandboxInstanceId self)
    {
        var baseName = SanitizeFolderName(rawName, self);
        var candidate = baseName;

        for (var suffix = 2; IsFolderNameTaken(candidate, self); suffix++)
        {
            candidate = $"{baseName} ({suffix})";
        }

        return candidate;
    }

    /// <summary>True when the folder exists and is owned by a different sandbox.</summary>
    private bool IsFolderNameTaken(string folderName, SandboxInstanceId self)
    {
        var folder = Path.Combine(_configDataFolder, folderName);
        if (!Directory.Exists(folder))
        {
            return false;
        }

        // An indexed path pointing at this folder for another sandbox wins.
        foreach (var (id, path) in _knownPaths)
        {
            if (id != self && PathsEqual(Path.GetDirectoryName(path), folder))
            {
                return true;
            }
        }

        var owner = FindManifestOwner(folder);

        // An existing folder we cannot attribute to this sandbox is never
        // clobbered (a folder with no readable manifest is treated as taken).
        return owner is null || owner.Value != self;
    }

    /// <summary>Returns the id of the manifest stored in a sandbox folder, if any.</summary>
    private static SandboxInstanceId? FindManifestOwner(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, ManifestSearchPattern, SearchOption.TopDirectoryOnly))
            {
                if (SandboxManifest.TryReadFile(file) is { } manifest)
                {
                    return new SandboxInstanceId(manifest.Id);
                }
            }
        }
        catch
        {
            // Unreadable folder: report no owner (caller treats it as taken).
        }

        return null;
    }

    /// <summary>
    /// Moves the whole sandbox folder (manifest and data disks) to
    /// <paramref name="targetFolder"/>, renaming the manifest to match the
    /// folder, and returns the new manifest path.
    /// </summary>
    private static string RelocateSandbox(string currentManifestPath, string targetFolder)
    {
        var sourceFolder = Path.GetDirectoryName(currentManifestPath)!;
        Directory.CreateDirectory(targetFolder);

        var targetManifestPath = Path.Combine(
            targetFolder,
            Path.GetFileName(targetFolder) + SandboxManifest.FileExtension);

        if (Directory.Exists(sourceFolder))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(sourceFolder))
            {
                var isManifest = string.Equals(entry, currentManifestPath, StringComparison.OrdinalIgnoreCase);
                var destination = isManifest
                    ? targetManifestPath
                    : Path.Combine(targetFolder, Path.GetFileName(entry));

                if (Directory.Exists(entry))
                {
                    Directory.Move(entry, destination);
                }
                else
                {
                    File.Move(entry, destination, overwrite: true);
                }
            }

            TryDeleteDirectoryIfEmpty(sourceFolder);
        }

        return targetManifestPath;
    }

    /// <summary>Removes a sandbox's manifest and, for a managed sandbox, its whole folder.</summary>
    private void DeleteSandbox(string manifestPath, SandboxInstanceId id)
    {
        var folder = Path.GetDirectoryName(manifestPath);
        if (string.IsNullOrEmpty(folder) || !IsManagedFolder(folder))
        {
            // Imported from an arbitrary location: only the file is ours.
            TryDelete(manifestPath);
            return;
        }

        // Never delete a folder that actually holds a different sandbox's manifest.
        if (Directory.Exists(folder) && !FolderBelongsTo(folder, id))
        {
            TryDelete(manifestPath);
            return;
        }

        TryDeleteDirectory(folder);
    }

    /// <summary>True when the folder holds this sandbox's manifest (or is empty).</summary>
    private static bool FolderBelongsTo(string folder, SandboxInstanceId id)
    {
        try
        {
            var manifests = Directory.EnumerateFiles(folder, ManifestSearchPattern, SearchOption.TopDirectoryOnly).ToArray();
            return manifests.Length == 0
                || manifests.Any(file => SandboxManifest.TryReadFile(file) is { } manifest && manifest.Id == id.Value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sanitizes a sandbox name for use as a folder/file name: strips invalid
    /// file-name characters, collapses whitespace runs to a single space, and
    /// trims trailing dots/spaces (illegal on Windows). An empty result falls
    /// back to the id.
    /// </summary>
    internal static string SanitizeFolderName(string? rawName, SandboxInstanceId id)
    {
        var fallback = id.Value.ToString("D");
        if (string.IsNullOrWhiteSpace(rawName))
        {
            return fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(rawName.Length);
        var lastWasSpace = false;

        foreach (var ch in rawName)
        {
            if (Array.IndexOf(invalid, ch) >= 0)
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        var result = builder.ToString().Trim();
        result = result.TrimEnd('.', ' ');

        return result.Length == 0 ? fallback : result;
    }

    /// <summary>True when the folder lies inside the config &amp; data folder (and is not the root itself).</summary>
    private bool IsManagedFolder(string folder)
        => IsWithinDirectory(folder, _configDataFolder) && !PathsEqual(folder, _configDataFolder);

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    // --------------------------- Portable transforms ---------------------------

    private static SandboxManifest ToPortable(SandboxInstance instance, string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        var dataFolder = instance.Recipe.Storage.HasCustomLocation
            ? instance.Recipe.Storage.StoragePath!
            : folder;

        var recipe = instance.Recipe with
        {
            Storage = instance.Recipe.Storage with { StoragePath = null },
            MappedFolders = instance.Recipe.MappedFolders
                .Select(f => new MappedFolder(MakeRelative(folder, f.HostFolder), f.SandboxFolder, f.ReadOnly))
                .ToArray(),
        };

        return SandboxManifest.FromInstance(instance) with
        {
            Recipe = recipe,
            DataPath = MakeRelative(folder, dataFolder),
        };
    }

    private static SandboxInstance FromPortable(SandboxManifest manifest, string path)
    {
        var folder = Path.GetDirectoryName(path)!;

        var recipe = manifest.Recipe with
        {
            Storage = manifest.Recipe.Storage with
            {
                StoragePath = ResolveRelative(folder, manifest.DataPath, folder),
            },
            MappedFolders = manifest.Recipe.MappedFolders
                .Select(f => new MappedFolder(ResolveRelative(folder, f.HostFolder, f.HostFolder), f.SandboxFolder, f.ReadOnly))
                .ToArray(),
        };

        // State/runtime are transient: never trust a live state from another
        // machine or a previous process. A loaded sandbox starts as Created.
        return SandboxInstance.Rehydrate(
            new SandboxInstanceId(manifest.Id),
            new SandboxName(manifest.Name),
            recipe,
            SandboxState.Created,
            runtime: null,
            faultReason: null,
            manifest.CreatedUtc,
            manifest.UpdatedUtc,
            manifest.Engine);
    }

    /// <summary>
    /// Stores <paramref name="path"/> relative to <paramref name="folder"/> when
    /// it lies inside it, otherwise absolute (so an external mapped folder keeps
    /// working).
    /// </summary>
    private static string MakeRelative(string folder, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return IsWithinDirectory(path, folder) ? Path.GetRelativePath(folder, path) : path;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>Resolves a relative path against <paramref name="folder"/>; absolute paths pass through.</summary>
    private static string ResolveRelative(string folder, string? path, string fallback)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return fallback;
        }

        try
        {
            return Path.IsPathFullyQualified(path) ? path : Path.GetFullPath(Path.Combine(folder, path));
        }
        catch
        {
            return path;
        }
    }

    private static bool IsWithinDirectory(string path, string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // --------------------------- IO helpers ---------------------------

    private static async Task<SandboxManifest?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return SandboxManifest.FromJson(json);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
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

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best effort: a locked folder is left in place.
        }
    }

    private static void TryDeleteDirectoryIfEmpty(string? directory)
    {
        try
        {
            if (!string.IsNullOrEmpty(directory)
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // Best effort: a non-empty or locked folder is left in place.
        }
    }
}
