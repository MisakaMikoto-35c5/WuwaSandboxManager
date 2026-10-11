using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Connection.RdpBase;

namespace Wsm.Connection.RdpHost.Internal;

/// <summary>
/// Default <see cref="IRdpSessionHost"/>. Spawns a dedicated STA thread, creates
/// the WinForms <see cref="RdpHostForm"/> and runs a message loop on it, then
/// completes when the window closes. The STA thread (and its message pump) is
/// required both by the ActiveX control and by the RDPBASE endpoint acquisition
/// the form performs.
/// </summary>
[ExposeServices(typeof(IRdpSessionHost))]
internal sealed class WinFormsRdpSessionHost : IRdpSessionHost, ISingletonDependency
{
    private readonly IRdpBaseConnectorFactory _connectorFactory;
    private readonly ILogger<WinFormsRdpSessionHost> _logger;

    public WinFormsRdpSessionHost(IRdpBaseConnectorFactory connectorFactory, ILogger<WinFormsRdpSessionHost> logger)
    {
        _connectorFactory = connectorFactory;
        _logger = logger;
    }

    public Task ShowAsync(RdpSessionRequest request, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new RdpHostForm(request, _connectorFactory, _logger);
                Application.Run(form);

                if (form.Failure is not null)
                {
                    completion.TrySetException(form.Failure);
                }
                else
                {
                    completion.TrySetResult();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The RDP ActiveX host thread failed.");
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Wsm.RdpHost",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task.WaitAsync(cancellationToken);
    }
}
