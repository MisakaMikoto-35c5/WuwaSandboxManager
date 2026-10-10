namespace WindowsSandboxManager.Domain;

/// <summary>Lifecycle state of a sandbox instance aggregate.</summary>
public enum SandboxState
{
    /// <summary>Defined but not yet started.</summary>
    Created = 0,

    Starting = 1,

    Running = 2,

    Rebooting = 3,

    Stopped = 4,

    Terminated = 5,

    Faulted = 6,
}

public static class SandboxStateExtensions
{
    public static bool IsLive(this SandboxState state)
        => state is SandboxState.Starting or SandboxState.Running or SandboxState.Rebooting;
}
