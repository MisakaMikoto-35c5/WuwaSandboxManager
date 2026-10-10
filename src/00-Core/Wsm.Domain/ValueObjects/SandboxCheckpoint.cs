namespace WindowsSandboxManager.Domain.ValueObjects;

/// <summary>Kind of HCS save used for a checkpoint.</summary>
public enum SandboxCheckpointType
{
    /// <summary>
    /// <c>SaveType = "ToFile"</c>: memory and device state are saved to the runtime
    /// state file, producing a resumable full-state checkpoint.
    /// </summary>
    FullState = 0,

    /// <summary>
    /// <c>SaveType = "AsTemplate"</c>: only device state is saved and the system is
    /// left in a state other systems can be cloned from (what Windows Sandbox uses
    /// internally for fast reboot).
    /// </summary>
    Template = 1,
}

/// <summary>
/// A saved checkpoint of a sandbox. Windows Sandbox keeps these under
/// <c>C:\ProgramData\Microsoft\Windows\Containers\Snapshots\&lt;id&gt;\SnapshotSavedState.vmrs</c>.
/// </summary>
public sealed record SandboxCheckpoint(
    Guid Id,
    string Name,
    SandboxCheckpointType Type,
    string StatePath,
    DateTimeOffset CreatedUtc)
{
    public override string ToString() => $"{Name} ({Type}) @ {StatePath}";
}
