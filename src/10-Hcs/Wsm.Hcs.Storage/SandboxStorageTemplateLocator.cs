namespace Wsm.Hcs.Storage;

/// <summary>
/// Locates a usable <b>source</b> for a sandbox's user-data storage. The
/// platform never ships a standalone user-data template on the machines we
/// inspected, so in practice this resolves either a donor container storage
/// (fast path: copy an existing sandbox) or nothing, in which case the caller
/// creates a blank dynamic VHDX from scratch.
/// </summary>
public sealed class SandboxStorageTemplateLocator
{
    /// <summary>File name of the writable user-data disk.</summary>
    public const string DiskFileName = "sandbox.vhdx";

    /// <summary>File name of the persistent guest-state file.</summary>
    public const string GuestStateFileName = "sandbox.vmgs";

    private readonly string _storageRoot;
    private readonly string? _templateRoot;

    /// <param name="storageRoot">
    /// The container storage root (<c>...\Containers\ContainerStorages</c>).
    /// </param>
    /// <param name="templateRoot">
    /// Optional directory holding a platform-shipped <c>sandbox.vhdx</c>
    /// template (or a single template sub-directory). No such template ships on
    /// current builds; this is a forward-compatible hook.
    /// </param>
    public SandboxStorageTemplateLocator(string storageRoot, string? templateRoot = null)
    {
        _storageRoot = storageRoot;
        _templateRoot = templateRoot;
    }

    /// <summary>
    /// First container storage directory that already holds a
    /// <c>sandbox.vhdx</c> — the preferred source when Windows Sandbox has run
    /// at least once. When several exist the <b>smallest</b> disk is chosen:
    /// an empty scratch is a few MB, whereas a layer's storage can be hundreds
    /// of MB, and a fresh sandbox should not inherit another sandbox's data.
    /// </summary>
    public string? FindDonorStorage() => FindWithDisk(_storageRoot, recurse: false);

    /// <summary>
    /// Every container storage directory under the root that holds a
    /// <c>sandbox.vhdx</c>, ordered by ascending disk size (the same preference
    /// <see cref="FindDonorStorage"/> uses). Used to walk differencing chains
    /// back to the base OS disk, where a single "smallest donor" is not enough:
    /// some leaves point at parents that no longer exist and must be skipped.
    /// </summary>
    public IReadOnlyList<string> EnumerateDonorStorages()
    {
        if (string.IsNullOrWhiteSpace(_storageRoot) || !Directory.Exists(_storageRoot))
        {
            return Array.Empty<string>();
        }

        try
        {
            var candidates = new List<string>();
            foreach (var directory in Directory.EnumerateDirectories(_storageRoot, "*", SearchOption.TopDirectoryOnly))
            {
                if (File.Exists(Path.Combine(directory, DiskFileName)))
                {
                    candidates.Add(directory);
                }
            }

            return candidates
                .OrderBy(d => new FileInfo(Path.Combine(d, DiskFileName)).Length)
                .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            // Access denied / IO races: treat as "no donors".
            return Array.Empty<string>();
        }
    }

    /// <summary>A platform-shipped template storage, if one exists.</summary>
    public string? FindTemplateStorage() => FindWithDisk(_templateRoot, recurse: true);

    /// <summary>Convenience: donor first, then template.</summary>
    public string? FindSourceStorage() => FindDonorStorage() ?? FindTemplateStorage();

    private static string? FindWithDisk(string? root, bool recurse)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        try
        {
            var candidates = new List<string>();
            if (File.Exists(Path.Combine(root, DiskFileName)))
            {
                candidates.Add(root);
            }

            var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var directory in Directory.EnumerateDirectories(root, "*", option))
            {
                if (File.Exists(Path.Combine(directory, DiskFileName)))
                {
                    candidates.Add(directory);
                }
            }

            return candidates
                .OrderBy(d => new FileInfo(Path.Combine(d, DiskFileName)).Length)
                .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            // Access denied / IO races: treat as "no source".
        }

        return null;
    }
}
