using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Host.Environment;

namespace Wsm.Connection.Session.Internal;

/// <summary>
/// Default <see cref="ISessionLauncher"/>. Launches the packaged
/// <c>WindowsSandboxRemoteSession.exe</c> client with <c>--id &lt;sandbox id&gt;</c>,
/// which selects full-desktop or RAIL presentation from the sandbox's own
/// <c>RailMode</c> flag.
/// </summary>
[ExposeServices(typeof(ISessionLauncher))]
internal sealed class SessionLauncher : ISessionLauncher, ISingletonDependency
{
    private readonly ISandboxEnvironment _environment;
    private readonly ILogger<SessionLauncher> _logger;

    public SessionLauncher(
        ISandboxEnvironment environment,
        ILogger<SessionLauncher> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public Task LaunchAsync(
        SandboxInstanceId id,
        SandboxRuntimeInfo runtime,
        SandboxConnectionMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode == SandboxConnectionMode.RemoteApp && !runtime.RailMode)
        {
            _logger.LogWarning(
                "Requested RemoteApp mode but sandbox {SandboxId} was started without RailMode. " +
                "RAIL is fixed at start time; restart the sandbox in RemoteApp mode to get seamless windows.", id);
        }

        var remoteSession = _environment.RemoteSessionExecutablePath;
        if (remoteSession is null)
        {
            throw new SandboxNotSupportedException(
                "The modern Windows Sandbox client executable (WindowsSandboxRemoteSession.exe) was not found.");
        }

        _logger.LogInformation("Connecting to sandbox {SandboxId} via WindowsSandboxRemoteSession.exe ({Mode})", id, mode);
        Process.Start(new ProcessStartInfo
        {
            FileName = remoteSession,
            Arguments = $"--id {id}",
            UseShellExecute = true,
        });
        return Task.CompletedTask;
    }
}
