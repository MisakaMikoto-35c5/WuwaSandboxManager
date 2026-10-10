using System.Collections.Concurrent;
using System.Text.Json;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.ValueObjects;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Sandbox;
using Wsm.Hcs.Layout;

namespace Wsm.Hcs.Checkpoint.Internal;

/// <summary>
/// Default <see cref="ICheckpointService"/>.
/// <para>
/// On-disk layout (scoped by system id):
/// <c>...\Containers\Snapshots\&lt;systemId&gt;\&lt;checkpointId&gt;\SnapshotSavedState.vmrs</c>.
/// The previous global layout (<c>...\Snapshots\&lt;checkpointId&gt;</c>) has been
/// replaced by this per-system layout. Snapshots are ephemeral, so there is no
/// migration for existing directories.
/// </para>
/// </summary>
[ExposeServices(typeof(ICheckpointService))]
internal sealed class CheckpointService : ICheckpointService, ISingletonDependency
{
    private readonly IHcsClient _hcs;
    private readonly IHcsHostLayout _layout;
    private readonly ISandboxVmJsonBuilder _json;

    /// <summary>
    /// Native <c>HCS_SYSTEM</c> handles for restored systems, keyed by the new
    /// compute-system id. The configuration sets
    /// <c>ShouldTerminateOnLastHandleClosed=true</c>, so the handle must be held
    /// for the restored instance's lifetime; <see cref="Release"/> (called by
    /// the engine's stop path) disposes it and terminates the system.
    /// </summary>
    private readonly ConcurrentDictionary<string, IDisposable> _systems =
        new(StringComparer.OrdinalIgnoreCase);

    public CheckpointService(IHcsClient hcs, IHcsHostLayout layout, ISandboxVmJsonBuilder json)
    {
        _hcs = hcs;
        _layout = layout;
        _json = json;
    }

    public SandboxCheckpoint Save(string systemId, string name, SandboxCheckpointType type)
    {
        var id = Guid.NewGuid();
        // Scope the checkpoint directory by the owning compute system id:
        //   <SnapshotsFolder>\<systemId>\<checkpointId>\SnapshotSavedState.vmrs
        var directory = Path.Combine(_layout.SnapshotsFolder, systemId, id.ToString());
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
            // Only enumerate this system's own snapshot folder.
            var systemRoot = Path.Combine(_layout.SnapshotsFolder, systemId);
            if (!Directory.Exists(systemRoot))
            {
                return results;
            }

            foreach (var directory in Directory.EnumerateDirectories(systemRoot))
            {
                var statePath = Path.Combine(directory, "SnapshotSavedState.vmrs");
                if (!File.Exists(statePath))
                {
                    continue;
                }

                var name = Path.GetFileName(directory);
                if (!Guid.TryParse(name, out var id))
                {
                    // Skip non-GUID directories gracefully.
                    continue;
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

        var system = _hcs.Create(newId, _json.Build(restored));
        try
        {
            _hcs.Start(newId);
        }
        catch
        {
            system.Dispose();
            throw;
        }

        // Hold the handle for the restored system's lifetime; releasing it (via
        // Release) is what terminates the system.
        if (_systems.TryRemove(newId, out var stale))
        {
            stale.Dispose();
        }

        _systems[newId] = system;
        return newId;
    }

    public void Release(string systemId)
    {
        if (_systems.TryRemove(systemId, out var system))
        {
            system.Dispose();
        }
    }
}
