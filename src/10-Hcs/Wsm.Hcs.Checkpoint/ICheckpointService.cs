using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Hcs.Json.Sandbox;

namespace Wsm.Hcs.Checkpoint;

/// <summary>
/// Manages HCS checkpoints for sandbox instances. A checkpoint is an
/// <c>HcsSaveComputeSystem</c> call with a <c>SaveOptions</c> document:
/// <list type="bullet">
///   <item><c>ToFile</c> saves memory + device state (resumable full-state checkpoint).</item>
///   <item><c>AsTemplate</c> saves device state only and leaves the system cloneable
///   (what Windows Sandbox uses internally for fast reboot).</item>
/// </list>
/// </summary>
public interface ICheckpointService
{
    SandboxCheckpoint Save(string systemId, string name, SandboxCheckpointType type);

    IReadOnlyList<SandboxCheckpoint> List(string systemId);

    /// <summary>
    /// Restores a full-state checkpoint into a brand new compute system by
    /// pointing <c>RestoreState.SaveStateFilePath</c> at the saved state.
    /// Returns the id of the new compute system.
    /// </summary>
    string Restore(HcsSandboxDefinition definition, SandboxCheckpoint checkpoint);
}
