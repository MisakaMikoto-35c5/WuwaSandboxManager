namespace Wsm.Service.Client;

/// <summary>
/// Starts a standalone (<c>--console</c>) WSM agent process on demand, so
/// clients can route through a resident agent on machines where the WsmAgent
/// Windows service is not installed.
/// </summary>
public interface IStandaloneAgentStarter
{
    /// <summary>
    /// Probes the agent and, when it is absent, spawns the standalone agent and
    /// waits for its pipe to come up. Returns true when an agent answers
    /// afterwards. Spawning is attempted at most once per process (a failed
    /// attempt is not retried); a non-elevated caller never spawns (the agent
    /// could not drive HCS anyway). Never throws.
    /// </summary>
    /// <param name="probe">The availability probe (typically the service client's own).</param>
    /// <param name="dataFolder">Optional <c>--data</c> folder override for the agent.</param>
    Task<bool> EnsureRunningAsync(
        Func<CancellationToken, Task<bool>> probe,
        string? dataFolder,
        CancellationToken ct = default);
}
