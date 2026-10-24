namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// Viewer attachment signalling for the frame agent's lifetime: each viewer creates a
/// <c>viewers\&lt;guid&gt;.lock</c> file next to the agent's files, touches it every
/// <see cref="TouchInterval"/> and deletes it on exit. The agent treats a lock whose
/// last write is older than <see cref="StaleAfter"/> as abandoned (a crashed viewer)
/// and exits once no live lock has been seen for <see cref="EmptyExitDelay"/>.
/// The agent implements the same rules in PowerShell; these constants are the contract.
/// </summary>
public static class ViewerLiveness
{
    /// <summary>Directory (under the agent directory) that holds the viewer locks.</summary>
    public const string LockDirectoryName = "viewers";

    /// <summary>Extension of a viewer lock file.</summary>
    public const string LockFileExtension = ".lock";

    /// <summary>A lock not touched for this long belongs to a dead viewer.</summary>
    public static TimeSpan StaleAfter { get; } = TimeSpan.FromSeconds(60);

    /// <summary>How often a viewer re-touches its lock.</summary>
    public static TimeSpan TouchInterval { get; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the agent survives without any live viewer lock.</summary>
    public static TimeSpan EmptyExitDelay { get; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// True while a lock's last write is within <see cref="StaleAfter"/> of now. A write
    /// in the future (clock disagreement across the silo) counts as live.
    /// </summary>
    public static bool IsLive(DateTime lastWriteUtc, DateTime nowUtc)
        => nowUtc - lastWriteUtc <= StaleAfter;

    /// <summary>True when no live viewer has been seen for longer than <see cref="EmptyExitDelay"/>.</summary>
    public static bool ShouldExit(DateTime lastViewerSeenUtc, DateTime nowUtc)
        => nowUtc - lastViewerSeenUtc > EmptyExitDelay;
}
