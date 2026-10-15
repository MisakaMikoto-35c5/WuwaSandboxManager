using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Connection.RdpHost;
using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.RdpHost.WinUi.Internal;

/// <summary>
/// WinUI 3 <see cref="IRdpConnectorHost"/>. It starts the dedicated connector
/// process with the request written to a temp JSON file (so the credential never
/// appears on the command line) and completes when that process exits — i.e. when
/// the user closes the WinUI window. <see cref="CloseAll"/> ends every live
/// connector process.
/// </summary>
[ExposeServices(typeof(IRdpConnectorHost))]
internal sealed class WinUiRdpConnectorHost : IRdpConnectorHost, ISingletonDependency
{
    private readonly ILogger<WinUiRdpConnectorHost> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Session> _sessions = new();

    public WinUiRdpConnectorHost(ILogger<WinUiRdpConnectorHost> logger)
    {
        _logger = logger;
    }

    public RdpConnectorKind Kind => RdpConnectorKind.WinUi;

    public async Task ShowAsync(RdpSessionRequest request, IRdpSandboxControl? control, CancellationToken cancellationToken = default)
    {
        var exe = WinUiConnectorLocator.Find()
            ?? throw new InvalidOperationException(
                "The WinUI 3 RDP connector (Wsm.Rdp.WinUI.exe) could not be located next to the host " +
                "or under src/50-App/Wsm.Rdp.WinUI/bin.");

        var requestPath = RdpSessionRequestFile.Save(request);
        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--request");
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Add("--parent-pid");
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start the WinUI 3 connector '{exe}'.");

        lock (_gate)
        {
            _sessions[request.SandboxId] = new Session(process, requestPath);
        }

        _logger.LogInformation("Started the WinUI 3 RDP connector for sandbox {SandboxId}.", request.SandboxId);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                _sessions.Remove(request.SandboxId);
            }

            RdpSessionRequestFile.TryDelete(requestPath);
            process.Dispose();
        }
    }

    public void CloseAll()
    {
        Session[] sessions;
        lock (_gate)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            TryKill(session.Process);
            RdpSessionRequestFile.TryDelete(session.RequestPath);
            session.Process.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // Best effort: a process that already exited is harmless.
        }
    }

    private sealed record Session(Process Process, string RequestPath);
}
