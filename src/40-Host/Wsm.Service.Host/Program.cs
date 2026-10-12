using System.ServiceProcess;
using Wsm.Service.Host;
using Wsm.Service.Host.Internal;

// WSM agent service entry point.
//
//   Wsm.Service.Host.exe --console          run in the foreground (development / verification)
//   Wsm.Service.Host.exe --data <folder>    use <folder> (absolute path) as the config & data
//                                           folder instead of the default
//                                           %USERPROFILE%\Documents\WsmContainers. `wsm service
//                                           install` writes the invoking user's WsmContainers
//                                           folder here so the LocalSystem service reads/writes
//                                           the user's .wsm definitions rather than the
//                                           LocalSystem profile (systemprofile).
//   Wsm.Service.Host.exe                    run under the Windows Service Control Manager (SCM)
//
// In both modes the agent owns the sandbox VM lifecycle and serves the
// wsm.service.WsmService gRPC contract over the named pipe \\.\pipe\wsm-agent.
var dataFolder = WsmServiceHost.ParseDataFolder(args);

if (args.Any(static a => string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase)))
{
    await WsmServiceHost.RunConsoleAsync(args, dataFolder);
    return;
}

ServiceBase.Run(new WsmWindowsService(args, dataFolder));
