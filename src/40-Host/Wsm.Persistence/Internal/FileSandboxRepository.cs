using System.Collections.Concurrent;
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
/// <c>&lt;configDataFolder&gt;\&lt;id&gt;\&lt;id&gt;.wsm</c> with its user-data
/// disks beside it. The repository also lists every <c>*.wsm</c> found
/// (recursively) under the config &amp; data folder and indexes any <c>.wsm</c>
/// opened from an arbitrary location (USB stick, another PC) for the session.
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
        var path = ResolvePath(instance.Id);
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
        TryDelete(path);
        _knownPaths.TryRemove(id, out _);
        TryDeleteDirectoryIfEmpty(Path.GetDirectoryName(path));
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

    private string ResolvePath(SandboxInstanceId id)
        => _knownPaths.TryGetValue(id, out var known)
            ? known
            : Path.Combine(_configDataFolder, id.Value.ToString("D"), $"{id.Value:D}{SandboxManifest.FileExtension}");

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
