using System.Text.Json;
using Volo.Abp.DependencyInjection;
using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Sandbox;
using Wsm.Hcs.Layout;

namespace Wsm.Hcs.Checkpoint.Internal;

/// <summary>
/// Default <see cref="ICheckpointService"/>. State files are stored under
/// <c>...\Containers\Snapshots\&lt;id&gt;\SnapshotSavedState.vmrs</c>, matching
/// the platform's own layout.
/// </summary>
[ExposeServices(typeof(ICheckpointService))]
internal sealed class CheckpointService : ICheckpointService, ISingletonDependency
{
    private readonly IHcsClient _hcs;
    private readonly IHcsHostLayout _layout;
    private readonly ISandboxVmJsonBuilder _json;

    public CheckpointService(IHcsClient hcs, IHcsHostLayout layout, ISandboxVmJsonBuilder json)
    {
        _hcs = hcs;
        _layout = layout;
        _json = json;
    }

    public SandboxCheckpoint Save(string systemId, string name, SandboxCheckpointType type)
    {
        var id = Guid.NewGuid();
        var directory = _layout.GetSnapshotDirectory(id.ToString());
        Directory.CreateDirectory(directory);
        var statePath = Path.Combine(directory, "SnapshotSavedState.vmrs");

        var options = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["SaveType"] = type == SandboxCheckpointType.Template ? "AsTemplate" : "ToFile",
            ["SaveStateFilePath"] = statePath,
        });

        _hcs.Save(systemId, options);
        return new SandboxCheckpoint(id, name, type, statePath, DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<SandboxCheckpoint> List(string systemId)
    {
        var results = new List<SandboxCheckpoint>();
        try
        {
            if (!Directory.Exists(_layout.SnapshotsRoot))
            {
                return results;
            }

            foreach (var directory in Directory.EnumerateDirectories(_layout.SnapshotsRoot))
            {
                var statePath = Path.Combine(directory, "SnapshotSavedState.vmrs");
                if (!File.Exists(statePath))
                {
                    continue;
                }

                var name = Path.GetFileName(directory);
                if (!Guid.TryParse(name, out var id))
                {
                    id = Guid.NewGuid();
                }

                results.Add(new SandboxCheckpoint(
                    id,
                    name,
                    SandboxCheckpointType.FullState,
                    statePath,
                    File.GetCreationTimeUtc(statePath)));
            }
        }
        catch
        {
            // ignore enumeration failures
        }

        return results.OrderByDescending(c => c.CreatedUtc).ToArray();
    }

    public string Restore(HcsSandboxDefinition definition, SandboxCheckpoint checkpoint)
    {
        var newId = Guid.NewGuid().ToString();
        var restored = definition with
        {
            Id = newId,
            RestoreStateFilePath = checkpoint.StatePath,
        };

        using var system = _hcs.Create(newId, _json.Build(restored));
        _hcs.Start(newId);
        return newId;
    }
}
