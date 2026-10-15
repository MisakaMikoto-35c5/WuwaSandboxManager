using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Connection.RdpBase;
using Wsm.Connection.RdpHost;
using Wsm.Domain.ValueObjects;
using Wsm.Localization;

namespace Wsm.Connection.RdpHost.Wpf.Internal;

/// <summary>
/// Default WPF <see cref="IRdpConnectorHost"/>. All windows live on one dedicated
/// STA thread that owns the single WPF <see cref="Application"/> for the process
/// (WPF allows only one per AppDomain). <see cref="ShowAsync"/> creates the
/// window on that dispatcher and completes when the window closes.
/// </summary>
[ExposeServices(typeof(IRdpConnectorHost))]
internal sealed class WpfRdpConnectorHost : IRdpConnectorHost, ISingletonDependency
{
    private readonly IRdpBaseConnectorFactory _connectorFactory;
    private readonly ILogger<WpfRdpConnectorHost> _logger;
    private readonly IStringLocalizer<WindowsSandboxManagerResource> _localizer;

    private readonly object _gate = new();
    private readonly object _startLock = new();
    private readonly List<RdpHostWindow> _windows = new();
    private Dispatcher? _dispatcher;

    public WpfRdpConnectorHost(
        IRdpBaseConnectorFactory connectorFactory,
        ILogger<WpfRdpConnectorHost> logger,
        IStringLocalizer<WindowsSandboxManagerResource> localizer)
    {
        _connectorFactory = connectorFactory;
        _logger = logger;
        _localizer = localizer;
    }

    public RdpConnectorKind Kind => RdpConnectorKind.Wpf;

    public Task ShowAsync(RdpSessionRequest request, IRdpSandboxControl? control, CancellationToken cancellationToken = default)
    {
        var dispatcher = EnsureDispatcher();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.InvokeAsync(() =>
        {
            try
            {
                var window = new RdpHostWindow(request, _connectorFactory, control, _logger, _localizer);
                lock (_gate)
                {
                    _windows.Add(window);
                }

                window.Closed += (_, _) =>
                {
                    lock (_gate)
                    {
                        _windows.Remove(window);
                    }

                    completion.TrySetResult();
                };

                window.Show();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to open the WPF RDP connector window.");
                completion.TrySetException(ex);
            }
        });

        return completion.Task.WaitAsync(cancellationToken);
    }

    public void CloseAll()
    {
        RdpHostWindow[] windows;
        lock (_gate)
        {
            windows = _windows.ToArray();
        }

        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        foreach (var window in windows)
        {
            try
            {
                dispatcher.InvokeAsync(() =>
                {
                    if (window.IsLoaded)
                    {
                        window.Close();
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Closing an RDP connector window failed (continuing).");
            }
        }
    }

    private Dispatcher EnsureDispatcher()
    {
        lock (_startLock)
        {
            if (_dispatcher is not null)
            {
                return _dispatcher;
            }

            using var ready = new ManualResetEventSlim(false);
            Dispatcher? created = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    created = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    application.Run();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The WPF RDP host thread failed.");
                    ready.Set();
                }
            })
            {
                IsBackground = true,
                Name = "Wsm.RdpHost.Wpf",
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            ready.Wait(TimeSpan.FromSeconds(30));
            _dispatcher = created
                ?? throw new InvalidOperationException("The WPF RDP host thread did not start.");
            return _dispatcher;
        }
    }
}
