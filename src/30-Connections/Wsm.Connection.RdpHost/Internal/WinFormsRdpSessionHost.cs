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
/// <para>
/// Open forms are tracked so <see cref="CloseAll"/> can end every live session
/// (e.g. before the owning sandbox is stopped) without waiting for the user to
/// close the window by hand.
/// </para>
/// </summary>
[ExposeServices(typeof(IRdpSessionHost))]
internal sealed class WinFormsRdpSessionHost : IRdpSessionHost, ISingletonDependency
{
    private readonly IRdpBaseConnectorFactory _connectorFactory;
    private readonly ILogger<WinFormsRdpSessionHost> _logger;

    private readonly object _formsGate = new();
    private readonly List<RdpHostForm> _forms = new();

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
            RdpHostForm? form = null;
            try
            {
                form = new RdpHostForm(request, _connectorFactory, _logger);
                lock (_formsGate)
                {
                    _forms.Add(form);
                }

                using (form)
                {
                    Application.Run(form);
                }

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
            finally
            {
                if (form is not null)
                {
                    lock (_formsGate)
                    {
                        _forms.Remove(form);
                    }
                }
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

    public void CloseAll()
    {
        RdpHostForm[] forms;
        lock (_formsGate)
        {
            forms = _forms.ToArray();
        }

        foreach (var form in forms)
        {
            try
            {
                if (form.IsHandleCreated && !form.IsDisposed && !form.Disposing)
                {
                    // Close on the form's own STA thread; its OnFormClosed then
                    // disconnects the mstscax client gracefully instead of
                    // leaving the guest session held.
                    form.BeginInvoke(new Action(form.Close));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Closing an RDP session window failed (continuing).");
            }
        }
    }
}
