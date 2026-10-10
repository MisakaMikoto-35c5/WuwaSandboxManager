using Microsoft.Extensions.Options;
using Microsoft.Win32;
using Volo.Abp.DependencyInjection;

namespace Wsm.Hcs.Layout.Internal;

/// <summary>
/// Default <see cref="IHcsHostLayout"/>: resolves the container stack's on-disk
/// layout and the registered layers. The platform containers tree stays at
/// <c>C:\ProgramData\Microsoft\Windows\Containers</c>; each sandbox's own data
/// lives under the user-selectable config &amp; data folder.
/// </summary>
[ExposeServices(typeof(IHcsHostLayout))]
internal sealed class HcsHostLayout : IHcsHostLayout, ISingletonDependency
{
    public const string DefaultPlatformContainersFolder = @"C:\ProgramData\Microsoft\Windows\Containers";

    /// <summary>
    /// Default config &amp; data folder: <c>%USERPROFILE%\Documents\WsmContainers</c>.
    /// </summary>
    public static string DefaultSandboxDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents",
        "WsmContainers");

    private const string LayersRegistryKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Containers\CmService\Layers";

    public HcsHostLayout(IOptions<HcsHostLayoutOptions> options)
    {
        var value = options.Value;

        PlatformContainersFolder = string.IsNullOrWhiteSpace(value.PlatformContainersFolder)
            ? DefaultPlatformContainersFolder
            : value.PlatformContainersFolder;

        SandboxDataFolder = string.IsNullOrWhiteSpace(value.SandboxDataFolder)
            ? DefaultSandboxDataFolder
            : value.SandboxDataFolder;
    }

    public string PlatformContainersFolder { get; }

    public string LayersFolder => Path.Combine(PlatformContainersFolder, "Layers");

    public string BaseImagesFolder => Path.Combine(PlatformContainersFolder, "BaseImages");

    public string PlatformContainerStoragesFolder => Path.Combine(PlatformContainersFolder, "ContainerStorages");

    public string SnapshotsFolder => Path.Combine(PlatformContainersFolder, "Snapshots");

    public string SandboxDataFolder { get; }

    public string GetStorageDirectory(string id) => Path.Combine(SandboxDataFolder, id);

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
            if (Directory.Exists(BaseImagesFolder))
            {
                foreach (var dir in Directory.EnumerateDirectories(BaseImagesFolder))
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

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out var parsed) => parsed,
        _ => 0,
    };
}
