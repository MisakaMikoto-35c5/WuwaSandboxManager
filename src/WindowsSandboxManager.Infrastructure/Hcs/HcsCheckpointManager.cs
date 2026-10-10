using System.Text.Json;
using WindowsSandboxManager.Domain.ValueObjects;

namespace WindowsSandboxManager.Infrastructure.Hcs;

/// <summary>
/// Manages HCS checkpoints for sandbox instances. A checkpoint is an
/// <c>HcsSaveComputeSystem</c> call with a <c>SaveOptions</c> document:
/// <list type="bullet">
///   <item><c>ToFile</c> saves memory + device state (resumable full-state checkpoint).</item>
///   <item><c>AsTemplate</c> saves device state only and leaves the system cloneable
///   (what Windows Sandbox uses internally for fast reboot).</item>
/// </list>
/// State files are stored under
/// <c>...\Containers\Snapshots\&lt;id&gt;\SnapshotSavedState.vmrs</c>, matching the
/// platform's own layout.
/// </summary>
public sealed class HcsCheckpointManager
{
    private readonly HcsService _hcs;
    private readonly HcsHostLayout _layout;

    public HcsCheckpointManager(HcsService hcs, HcsHostLayout layout)
    {
        _hcs = hcs;
        _layout = layout;
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

    /// <summary>
    /// Restores a full-state checkpoint into a brand new compute system by
    /// pointing <c>RestoreState.SaveStateFilePath</c> at the saved state.
    /// Returns the id of the new compute system.
    /// </summary>
    public string Restore(HcsSandboxDefinition definition, SandboxCheckpoint checkpoint)
    {
        var newId = Guid.NewGuid().ToString();
        var restored = definition with
        {
            Id = newId,
            RestoreStateFilePath = checkpoint.StatePath,
        };

        using var system = _hcs.Create(newId, HcsSandboxJson.Build(restored));
        _hcs.Start(newId);
        return newId;
    }
}
