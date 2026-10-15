using System.Text.Json;
using Volo.Abp.DependencyInjection;

namespace Wsm.Hcs.Json.Container.Internal;

/// <summary>
/// Default <see cref="IContainerJsonBuilder"/>. Emits the HCS schema 2.1
/// <c>Container</c> object for a <b>process-isolated</b> (Argon) container, as
/// observed in the platform's own container runtime (<c>hcsshim</c>):
/// <list type="bullet">
///   <item><c>GuestOs.HostName</c> — the container host name;</item>
///   <item><c>Storage.Path</c> — the activated scratch layer's
///   <c>\\?\Volume{GUID}\</c> mount path, which becomes the container's
///   <c>C:\</c>;</item>
///   <item><c>Storage.Layers[]</c> — the read-only image layers (<c>Id</c> +
///   <c>Path</c>), base first;</item>
///   <item><c>Memory.SizeInMB</c> / <c>Processor.Count</c> — resource limits;</item>
///   <item><c>MappedDirectories</c> — host folders bound into the container;</item>
///   <item><c>Networking.Namespace</c> — the HNS namespace that carries the
///   container's NIC.</item>
/// </list>
/// Optional sections are omitted when they carry no data, because several HCS
/// builds reject empty-but-present objects at construct time.
/// </summary>
[ExposeServices(typeof(IContainerJsonBuilder))]
internal sealed class ContainerJsonBuilder : IContainerJsonBuilder, ITransientDependency
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    public string Build(ContainerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (string.IsNullOrWhiteSpace(definition.ScratchMountPath))
        {
            throw new ArgumentException(
                "Container.Storage.Path (the activated scratch layer's mount path) is required.",
                nameof(definition));
        }

        var container = new Dictionary<string, object?>
        {
            ["GuestOs"] = new Dictionary<string, object?>
            {
                ["HostName"] = string.IsNullOrWhiteSpace(definition.HostName)
                    ? definition.Id
                    : definition.HostName,
            },
            ["Storage"] = new Dictionary<string, object?>
            {
                // Storage.Path is the mounted scratch volume (the container's C:),
                // and Layers are the read-only image layers unioned beneath it.
                ["Path"] = definition.ScratchMountPath,
                ["Layers"] = definition.Layers.Select(layer => (object)new Dictionary<string, object?>
                {
                    ["Id"] = layer.Id,
                    ["Path"] = layer.Path,
                }).ToArray(),
            },
        };

        // Only advertise a limit when the caller asked for one; a value of 0
        // means "inherit the host default".
        if (definition.MemoryMb > 0)
        {
            container["Memory"] = new Dictionary<string, object?>
            {
                ["SizeInMB"] = definition.MemoryMb,
            };
        }

        if (definition.CpuCount > 0)
        {
            container["Processor"] = new Dictionary<string, object?>
            {
                ["Count"] = definition.CpuCount,
            };
        }

        if (definition.MappedFolders.Count > 0)
        {
            container["MappedDirectories"] = definition.MappedFolders
                .Select(folder => (object)new Dictionary<string, object?>
                {
                    ["HostPath"] = folder.HostPath,
                    ["ContainerPath"] = folder.ContainerPath,
                    ["ReadOnly"] = folder.ReadOnly,
                })
                .ToArray();
        }

        // The container joins an HNS namespace; the endpoint added to it supplies
        // the NIC. Omitting the section leaves the container with no networking.
        if (!string.IsNullOrWhiteSpace(definition.NetworkNamespace))
        {
            container["Networking"] = new Dictionary<string, object?>
            {
                ["Namespace"] = definition.NetworkNamespace,
            };
        }

        var root = new Dictionary<string, object?>
        {
            ["SchemaVersion"] = new Dictionary<string, object?>
            {
                ["Major"] = definition.SchemaMajor,
                ["Minor"] = definition.SchemaMinor,
            },
            ["Owner"] = definition.Owner,
            ["ShouldTerminateOnLastHandleClosed"] = true,
            ["Container"] = container,
        };

        return JsonSerializer.Serialize(root, Options);
    }
}
