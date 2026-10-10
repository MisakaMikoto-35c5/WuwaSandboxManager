using System.Security.Principal;
using Microsoft.Win32;
using Volo.Abp.DependencyInjection;

namespace Wsm.Hcs.Layout.Internal;

/// <summary>
/// Default <see cref="IHcsHostLayout"/>: resolves the container stack's on-disk
/// layout, the registered layers and the process elevation state.
/// </summary>
[ExposeServices(typeof(IHcsHostLayout))]
internal sealed class HcsHostLayout : IHcsHostLayout, ISingletonDependency
{
    public const string DefaultDataRoot = @"C:\ProgramData\Microsoft\Windows\Containers";
    private const string LayersRegistryKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Containers\CmService\Layers";

    public HcsHostLayout(string? dataRoot = null)
        => DataRoot = dataRoot ?? DefaultDataRoot;

    public string DataRoot { get; }

    public string LayersRoot => Path.Combine(DataRoot, "Layers");

    public string BaseImagesRoot => Path.Combine(DataRoot, "BaseImages");

    public string StorageRoot => Path.Combine(DataRoot, "ContainerStorages");

    public string SnapshotsRoot => Path.Combine(DataRoot, "Snapshots");

    public string GetStorageDirectory(string id) => Path.Combine(StorageRoot, id);

    public string GetStorageDiskPath(string id) => Path.Combine(GetStorageDirectory(id), "sandbox.vhdx");

    public string GetGuestStatePath(string id) => Path.Combine(GetStorageDirectory(id), "sandbox.vmgs");

    public string GetSnapshotDirectory(string id) => Path.Combine(SnapshotsRoot, id);

    public string GetSnapshotStatePath(string id) => Path.Combine(GetSnapshotDirectory(id), "SnapshotSavedState.vmrs");

    /// <summary>True when the process can call HCS (administrator / Hyper-V admins).</summary>
    public bool IsElevated => IsProcessElevated();

    /// <summary>Enumerates the registered layers (newest sequence number first).</summary>
    public IReadOnlyList<HcsLayerInfo> ListLayers()
    {
        var layers = new List<HcsLayerInfo>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(LayersRegistryKey);
            if (key is null)
            {
                return layers;
            }

            foreach (var name in key.GetSubKeyNames())
            {
                if (!Guid.TryParse(name, out var id))
                {
                    continue;
                }

                using var sub = key.OpenSubKey(name);
                var path = sub?.GetValue("Path") as string;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                layers.Add(new HcsLayerInfo
                {
                    Id = id,
                    Path = path,
                    OsVersion = sub?.GetValue("OSVersion") as string,
                    ParentType = ToInt(sub?.GetValue("ParentType")),
                    ParentId = Guid.TryParse(sub?.GetValue("ParentId") as string, out var parent) ? parent : Guid.Empty,
                    SequenceNumber = ToInt(sub?.GetValue("SequenceNumber")),
                    State = ToInt(sub?.GetValue("State")),
                });
            }
        }
        catch
        {
            // Registry access can fail in constrained environments.
        }

        return layers.OrderByDescending(l => l.SequenceNumber).ToArray();
    }

    /// <summary>
    /// Resolves the most recent usable base OS image layer (the layer whose
    /// <c>Files</c> folder contains a <c>Windows</c> directory). This is what the
    /// sandbox exposes to the guest through <c>VirtualSmb.Shares[0]</c> and
    /// <c>RunInSilo.SiloBaseOsPath</c>.
    /// </summary>
    public string? ResolveBaseLayerPath()
    {
        var layer = ListLayers().FirstOrDefault(l => l.IsUsableBaseImage);
        if (layer is not null)
        {
            return layer.FilesPath;
        }

        // Fall back to a BaseImages\<id>\BaseLayer\Files layout if present.
        try
        {
            if (Directory.Exists(BaseImagesRoot))
            {
                foreach (var dir in Directory.EnumerateDirectories(BaseImagesRoot))
                {
                    var candidate = Path.Combine(dir, "BaseLayer", "Files");
                    if (Directory.Exists(Path.Combine(candidate, "Windows")))
                    {
                        return candidate;
                    }
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static bool IsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => 0,
    };
}
