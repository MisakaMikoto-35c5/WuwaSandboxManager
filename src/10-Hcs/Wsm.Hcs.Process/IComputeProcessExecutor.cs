namespace Wsm.Hcs.Process;

/// <summary>
/// Executes a process inside an HCS compute system.
/// <b>Reserved / not implemented yet.</b>
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
