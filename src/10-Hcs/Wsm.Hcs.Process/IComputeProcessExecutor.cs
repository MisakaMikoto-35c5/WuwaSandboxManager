namespace Wsm.Hcs.Process;

/// <summary>
/// Executes a process inside an HCS compute system. This is the primitive used
/// by the Windows container engine to run commands (and start services) inside a
/// container, including one built from the shared Windows Sandbox image.
/// </summary>
public interface IComputeProcessExecutor
{
    /// <summary>Runs <paramref name="commandLine"/> inside the compute system and returns its exit code.</summary>
    Task<int> ExecuteAsync(
        string computeSystemId,
        string commandLine,
        string? workingDirectory = null,
        CancellationToken ct = default);
}
