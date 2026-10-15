using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Connection.RdpBase;

namespace Wsm.Connection.RailClient.Internal;

/// <summary>
/// Default <see cref="IRailClientSessionHost"/>. Spawns a dedicated STA thread,
/// creates the <see cref="RailClientForm"/> (the WDAG-style client dialog that
/// brokers the session) and runs a message loop on it, then completes when the
/// window closes. The STA thread (and its message pump) is required both by the
/// ActiveX control and by the RDPBASE endpoint acquisition the form performs.
/// <para>
/// Open forms are tracked so <see cref="CloseAll"/> can end every live session
/// (e.g. before the owning sandbox is stopped) without waiting for the user to
/// close the window by hand.
/// </para>
/// </summary>
[ExposeServices(typeof(IRailClientSessionHost))]
internal sealed class WinFormsRailClientSessionHost : IRailClientSessionHost, ISingletonDependency
{
    private readonly IRdpBaseConnectorFactory _connectorFactory;
    private readonly ILogger<WinFormsRailClientSessionHost> _logger;

    private readonly object _formsGate = new();
    private readonly List<RailClientForm> _forms = new();

    public WinFormsRailClientSessionHost(
        IRdpBaseConnectorFactory connectorFactory,
        ILogger<WinFormsRailClientSessionHost> logger)
    {
        _connectorFactory = connectorFactory;
        _logger = logger;
    }

    public Task ShowAsync(RailClientSessionRequest request, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            RailClientForm? form = null;
            try
            {
                form = new RailClientForm(request, _connectorFactory, _logger);
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
                _logger.LogError(ex, "The RemoteApp client thread failed.");
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
            Name = "Wsm.RailClient",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return completion.Task.WaitAsync(cancellationToken);
    }

    public void CloseAll()
    {
        RailClientForm[] forms;
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
                _logger.LogDebug(ex, "Closing a RemoteApp client window failed (continuing).");
            }
        }
    }
}
