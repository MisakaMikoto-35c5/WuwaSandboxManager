using System.Text.Json;
using Volo.Abp.DependencyInjection;

namespace Wsm.Hcs.Json.Container.Internal;

/// <summary>
/// Default <see cref="IContainerJsonBuilder"/>. Emits the HCS schema 2.1
/// <c>Container</c> object observed in the platform's own container runtime
/// (<c>hcsshim</c>/<c>dockerd</c>):
/// <list type="bullet">
///   <item><c>GuestOs.HostName</c> — the container host name;</item>
///   <item><c>Storage.Path</c> — the writable scratch volume;</item>
///   <item><c>Storage.Layers</c> — the read-only base OS image layer, which is
///   the <b>same minimal image the Windows Sandbox uses</b>;</item>
///   <item><c>Memory.SizeInMB</c> / <c>Processor.Count</c> — resource limits;</item>
///   <item><c>MappedDirectories</c> — host folders bound into the container;</item>
///   <item><c>Networking.NetworkAdapters</c> — HNS endpoint ids.</item>
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

        // HCS expects a unique id per layer; emit it when the caller resolved it
        // (the Windows Sandbox layer directory embeds it as `Layers\<guid>`).
        var layer = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(definition.BaseLayerId))
        {
            layer["Id"] = definition.BaseLayerId;
        }

        layer["Path"] = definition.BaseLayerPath;

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
                // Storage.Path is the container's writable scratch; Layers holds
                // the read-only image layers it is merged with. Sharing the
                // Windows Sandbox base layer here is what lets both engine kinds
                // run off a single provisioned image.
                ["Path"] = definition.ScratchPath,
                ["Layers"] = new object[] { layer },
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

        // A NetworkAdapters entry must be a real HNS endpoint GUID. When the
        // caller did not supply one (no HNS network is configured) the whole
        // Networking object is omitted so the container simply gets no NIC.
        if (definition.EnableNetworking && definition.NetworkAdapterIds.Count > 0)
        {
            container["Networking"] = new Dictionary<string, object?>
            {
                ["NetworkAdapters"] = definition.NetworkAdapterIds.ToArray(),
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
