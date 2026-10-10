namespace Wsm.Persistence;

/// <summary>Options for <see cref="ISandboxInstanceRepository"/> storage location.</summary>
public sealed class SandboxPersistenceOptions
{
    /// <summary>
    /// Root directory that holds the <c>instances</c> and <c>recipes</c> folders.
    /// When unset, defaults to <c>%LOCALAPPDATA%\WindowsSandboxManager</c>.
    /// </summary>
    public string? RootDirectory { get; set; }
}
