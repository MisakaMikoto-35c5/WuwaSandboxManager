using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Host.Elevation;

namespace Wsm.Service.Client.Internal;

/// <summary>
/// Default <see cref="IStandaloneAgentStarter"/>: spawns
/// <c>Wsm.Service.Host.exe --console</c> hidden and polls the agent pipe until it
/// answers. The spawned agent is a normal child process of the caller's session
/// and keeps running (holding its VMs) after the client exits.
/// </summary>
[ExposeServices(typeof(IStandaloneAgentStarter))]
internal sealed class StandaloneAgentStarter : IStandaloneAgentStarter, ISingletonDependency
{
    private readonly IElevationService _elevation;
    private readonly ILogger<StandaloneAgentStarter> _logger;
    private int _attempted;

    public StandaloneAgentStarter(IElevationService elevation, ILogger<StandaloneAgentStarter> logger)
    {
        _elevation = elevation;
        _logger = logger;
    }

    /// <summary>Total time to wait for the spawned agent's pipe to come up. Settable from tests.</summary>
    internal TimeSpan BootTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Interval between readiness probes while the agent boots. Settable from tests.</summary>
    internal TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Process spawn hook; <c>null</c> uses <see cref="Process.Start(ProcessStartInfo)"/>. Settable from tests.</summary>
    internal Func<ProcessStartInfo, Process?>? ProcessStarter { get; set; }

    /// <summary>Host executable locator hook; <c>null</c> uses <see cref="ServiceHostExeLocator.Find"/>. Settable from tests.</summary>
    internal Func<string?>? ExeLocator { get; set; }

    public async Task<bool> EnsureRunningAsync(
        Func<CancellationToken, Task<bool>> probe,
        string? dataFolder,
        CancellationToken ct = default)
    {
        try
        {
            if (await probe(ct).ConfigureAwait(false))
            {
                return true;
            }

            if (Interlocked.Exchange(ref _attempted, 1) != 0)
            {
                // A previous attempt failed (or is still booting): do not pile up
                // agent processes.
                _logger.LogDebug("Standalone agent start was already attempted in this process; not retrying.");
                return false;
            }

            if (!_elevation.IsElevated)
            {
                // The agent could not drive HCS unelevated, so spawning it would
                // only produce a dead process.
                _logger.LogDebug("Not spawning the standalone agent: the current process is not elevated.");
                return false;
            }

            var exe = (ExeLocator ?? ServiceHostExeLocator.Find)();
            if (exe is null)
            {
                _logger.LogWarning("Not spawning the standalone agent: Wsm.Service.Host.exe was not found.");
                return false;
            }

            var startInfo = new ProcessStartInfo(exe)
            {
                // ShellExecute (not CreateProcess): CreateProcess always passes
                // bInheritHandles=TRUE, so the agent would inherit every
                // inheritable handle of this process — including a caller's
                // captured stdout/stderr pipe (PowerShell 2>&1, CI), which would
                // then never reach EOF while the agent lives. ShellExecuteEx
                // inherits nothing; the hidden window keeps the console agent
                // invisible. Its console output is discarded (no reader); the
                // agent is a console app that only logs there.
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            startInfo.ArgumentList.Add("--console");
            if (!string.IsNullOrEmpty(dataFolder))
            {
                startInfo.ArgumentList.Add("--data");
                startInfo.ArgumentList.Add(dataFolder);
            }

            using var process = (ProcessStarter ?? Process.Start)(startInfo);
            if (process is null)
            {
                _logger.LogWarning("Failed to spawn the standalone agent from {Exe}.", exe);
                return false;
            }

            _logger.LogInformation("Spawned standalone agent {Exe} (pid {Pid}); waiting for the pipe.", exe, process.Id);

            var deadline = DateTime.UtcNow + BootTimeout;
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    _logger.LogWarning("The standalone agent exited during boot (exit code {ExitCode}).", process.ExitCode);
                    return false;
                }

                if (await probe(ct).ConfigureAwait(false))
                {
                    return true;
                }

                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }

            var ready = await probe(ct).ConfigureAwait(false);
            if (!ready)
            {
                _logger.LogWarning("The standalone agent (pid {Pid}) did not answer on the pipe within {Timeout}.", process.Id, BootTimeout);
            }

            return ready;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spawning the standalone agent failed; continuing without the agent.");
            return false;
        }
    }
}
