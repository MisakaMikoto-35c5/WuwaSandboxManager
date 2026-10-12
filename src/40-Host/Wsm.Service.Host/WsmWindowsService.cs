using System.ServiceProcess;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wsm.Service.Host.Internal;

namespace Wsm.Service.Host;

/// <summary>
/// Windows Service (SCM) host for the WSM agent. The service keeps the
/// ABP + Kestrel host alive for its whole lifetime so the sandbox VMs — whose
/// running references are process-local — survive across client calls.
/// </summary>
/// <remarks>
/// Built for <c>net10.0-windows</c>; the SCM starts it without arguments (see
/// <c>Program.cs</c>). Pass <c>--console</c> to run the same host interactively.
/// </remarks>
public sealed class WsmWindowsService : ServiceBase
{
    private readonly string[] _args;
    private readonly WsmServiceHost.DataFolderArgument _dataFolder;
    private WebApplication? _host;

    public WsmWindowsService(string[] args, WsmServiceHost.DataFolderArgument dataFolder)
    {
        _args = args;
        _dataFolder = dataFolder;
        ServiceName = "WsmAgent";
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    /// <summary>Starts the agent host. Must return promptly so the SCM does not time out.</summary>
    protected override void OnStart(string[] args)
    {
        // The SCM does not forward the binPath arguments to OnStart, so the process
        // command line parsed by Program.cs is used (it carries --console / --data).
        _host = WsmServiceHost.BuildAsync(_args, _dataFolder).GetAwaiter().GetResult();
        _host.StartAsync().GetAwaiter().GetResult();

        _host.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Wsm.Service.Host")
            .LogInformation(@"Wsm agent listening on \\.\pipe\{PipeName} (service mode).", WsmServiceHost.PipeName);
    }

    /// <summary>Stops and disposes the agent host gracefully.</summary>
    protected override void OnStop()
    {
        if (_host is null)
        {
            return;
        }

        _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _host = null;
    }
}
