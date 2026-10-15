using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Volo.Abp;
using Volo.Abp.Autofac;
using Wsm.Connection.RdpBase;
using Wsm.Connection.RdpHost;
using Wsm.Localization;

namespace Wsm.Rdp.WinUI;

/// <summary>
/// WinUI 3 application host for the RDP connector. Boots the ABP module graph
/// (application layer + engines + localization) so the toolbar's lifecycle
/// buttons can drive the sandbox, then shows the connector window for the
/// request handed over by the launching host.
/// </summary>
public partial class App : Microsoft.UI.Xaml.Application
{
    private IAbpApplicationWithInternalServiceProvider? _abp;
    private ConnectorWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = ConnectorArguments.Parse(Environment.GetCommandLineArgs());
        if (arguments is null)
        {
            Exit();
            return;
        }

        var request = RdpSessionRequestFile.Load(arguments.RequestPath);
        RdpSessionRequestFile.TryDelete(arguments.RequestPath);

        _abp = await AbpApplicationFactory.CreateAsync<RdpConnectorModule>(options => options.UseAutofac());
        await _abp.InitializeAsync();
        var services = _abp.ServiceProvider;

        _window = new ConnectorWindow(
            request,
            services.GetService<IRdpSandboxControl>(),
            services.GetRequiredService<IRdpBaseConnectorFactory>(),
            services.GetRequiredService<IStringLocalizer<WindowsSandboxManagerResource>>());
        _window.Closed += OnWindowClosed;
        _window.Activate();

        if (arguments.ParentProcessId is int parentProcessId)
        {
            WatchParent(parentProcessId);
        }
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        await ShutdownAsync();
    }

    /// <summary>
    /// Ends this connector when the host that spawned it exits, so cancelling a
    /// session in the manager (which kills the host) cannot leave the connector
    /// window orphaned.
    /// </summary>
    private static void WatchParent(int parentProcessId)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var thread = new Thread(() =>
        {
            try
            {
                using var parent = Process.GetProcessById(parentProcessId);
                parent.WaitForExit();
            }
            catch
            {
                // The parent is already gone.
            }

            dispatcher.TryEnqueue(() =>
            {
                try
                {
                    Microsoft.UI.Xaml.Application.Current.Exit();
                }
                catch
                {
                    // Best effort.
                }
            });
        })
        {
            IsBackground = true,
            Name = "Wsm.Rdp.WinUI.ParentWatch",
        };

        thread.Start();
    }

    private async Task ShutdownAsync()
    {
        var abp = _abp;
        _abp = null;
        if (abp is null)
        {
            return;
        }

        await abp.ShutdownAsync();
        abp.Dispose();
    }
}
