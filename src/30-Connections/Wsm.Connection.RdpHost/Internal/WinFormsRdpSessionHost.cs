using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;

namespace Wsm.Connection.RdpHost.Internal;

/// <summary>
/// Default <see cref="IRdpSessionHost"/>. Spawns a dedicated STA thread, creates
/// the WinForms <see cref="RdpHostForm"/> and runs a message loop on it, then
/// completes when the window closes. A separate STA thread is required because
/// the ActiveX control needs an STA message pump and the caller may be an MTA
/// background thread.
/// <para>
/// If the form could not present the session (for example
/// <c>ConnectWithEndpoint</c> was rejected) the completion task is faulted with
/// the specific exception, so the caller can surface it and fall back.
/// </para>
/// </summary>
[ExposeServices(typeof(IRdpSessionHost))]
internal sealed class WinFormsRdpSessionHost : IRdpSessionHost, ISingletonDependency
{
    private readonly ILogger<WinFormsRdpSessionHost> _logger;

    public WinFormsRdpSessionHost(ILogger<WinFormsRdpSessionHost> logger)
    {
        _logger = logger;
    }

    public Task ShowAsync(RdpSessionRequest request, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                using var form = new RdpHostForm(request, _logger);
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
