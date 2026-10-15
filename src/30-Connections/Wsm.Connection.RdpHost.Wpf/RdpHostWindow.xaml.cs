using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Wsm.Connection.RdpBase;
using Wsm.Connection.RdpHost.Ax;
using Wsm.Domain.ValueObjects;
using Wsm.Localization;

namespace Wsm.Connection.RdpHost.Wpf;

/// <summary>
/// The WPF RDP connector window. It presents a toolbar with the quick lifecycle
/// controls (Ctrl+Alt+Del, restart, shut down, force shut down, power on) and a
/// full-screen toggle, and hosts the shared <c>mstscax</c> ActiveX control in a
/// <see cref="System.Windows.Forms.Integration.WindowsFormsHost"/> that fills the
/// window and therefore resizes with it.
/// </summary>
public partial class RdpHostWindow : Window
{
    private static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(30);

    private readonly IRdpBaseConnectorFactory _connectorFactory;
    private readonly IRdpSandboxControl? _control;
    private readonly ILogger _logger;
    private readonly IStringLocalizer<WindowsSandboxManagerResource> _l;
    private readonly CancellationTokenSource _closeCts = new();
    private readonly DispatcherTimer _resizeTimer;

    private RdpSessionRequest _request;
    private RdpClientControl? _rdpControl;
    private IRdpBaseConnector? _connector;
    private bool _busy;
    private bool _fullScreen;
    private WindowState _previousState = WindowState.Normal;
    private WindowStyle _previousStyle = WindowStyle.SingleBorderWindow;
    private ResizeMode _previousResize = ResizeMode.CanResize;

    public RdpHostWindow(
        RdpSessionRequest request,
        IRdpBaseConnectorFactory connectorFactory,
        IRdpSandboxControl? control,
        ILogger logger,
        IStringLocalizer<WindowsSandboxManagerResource> localizer)
    {
        _request = request;
        _connectorFactory = connectorFactory;
        _control = control;
        _logger = logger;
        _l = localizer;

        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(request.WindowTitle) ? "Windows Sandbox" : request.WindowTitle;

        _resizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _resizeTimer.Tick += OnResizeTimerTick;

        ApplyLocalization();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closeCts.Cancel();
        TearDownClient();
        base.OnClosed(e);
    }

    private void ApplyLocalization()
    {
        CtrlAltDelButton.Content = Text("RdpConnector.CtrlAltDel", "Ctrl+Alt+Del");
        RestartButton.Content = Text("RdpConnector.Restart", "Restart");
        ShutdownButton.Content = Text("RdpConnector.Shutdown", "Shut down");
        ForceShutdownButton.Content = Text("RdpConnector.ForceShutdown", "Force shut down");
        PowerOnButton.Content = Text("RdpConnector.PowerOn", "Power on");
        FullScreenButton.Content = Text("RdpConnector.FullScreen", "Full screen");
        OverlayCloseButton.Content = Text("RdpConnector.Close", "Close");
    }

    private string Text(string key, string fallback)
    {
        var value = _l[key];
        return value.ResourceNotFound ? fallback : value.Value;
    }

    // ----------------------------- Connect -----------------------------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_request.FullScreen)
        {
            EnterFullScreen();
        }

        await ConnectCoreAsync();
    }

    private async Task ConnectCoreAsync()
    {
        try
        {
            SetStatus(Text("RdpConnector.Connecting", "Connecting…"));

            var rdpControl = new RdpClientControl();
            rdpControl.Connected += OnSessionConnected;
            rdpControl.Disconnected += OnSessionDisconnected;
            rdpControl.FatalError += OnSessionFatalError;
            rdpControl.LogonError += OnSessionLogonError;

            _rdpControl = rdpControl;
            RdpHost.Child = rdpControl;

            await rdpControl.ConnectAsync(_request, AcquireEndpointAsync, _closeCts.Token);

            rdpControl.Focus();
            UpdateToolbarState();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to present the RDP session through the WPF connector.");
            ShowOverlay(
                Text("RdpConnector.LostTitle", "Connection lost"),
                Text("RdpConnector.ConnectFailedBody", "The session could not be presented."),
                ex.Message);
        }
    }

    private async Task<object> AcquireEndpointAsync(CancellationToken cancellationToken)
    {
        _connector = _connectorFactory.Create();
        return await _connector
            .GetEndpointAsync(_request.PipeName, cancellationToken)
            .WaitAsync(EndpointTimeout, cancellationToken)
            .ConfigureAwait(true);
    }

    private async Task ReconnectAsync()
    {
        TearDownClient();
        HideOverlay();
        await ConnectCoreAsync();
    }

    // ----------------------------- Session events -----------------------------

    private void OnSessionConnected(object? sender, EventArgs e)
    {
        SetStatus(Text("RdpConnector.Connected", "Connected"));
        UpdateToolbarState();
        Dispatcher.InvokeAsync(() => _rdpControl?.UpdateRemoteDesktopSize());
    }

    private void OnSessionDisconnected(object? sender, RdpDisconnectEventArgs e)
    {
        _logger.LogInformation("RDP session disconnected (reason {Reason}).", e.Reason);
        UpdateToolbarState();
        ShowOverlay(
            Text("RdpConnector.DisconnectedTitle", "Connection disconnected"),
            Text("RdpConnector.DisconnectedBody", "The connection to the sandbox has been disconnected."),
            $"Reason: {e.ReasonText} (code {e.Reason}).");
    }

    private void OnSessionFatalError(object? sender, int errorCode)
    {
        _logger.LogError("RDP session fatal error {Error}.", errorCode);
        UpdateToolbarState();
        ShowOverlay(
            Text("RdpConnector.LostTitle", "Connection lost"),
            Text("RdpConnector.LostBody", "The connection to the sandbox was lost because of a fatal error."),
            $"Error code {errorCode}.");
    }

    private void OnSessionLogonError(object? sender, int errorCode)
    {
        // Arbitration codes (-2..-7) are logon progress events, not errors.
        if (errorCode is >= -7 and <= -2)
        {
            _logger.LogInformation("RDP logon arbitration code {Code}.", errorCode);
        }
        else
        {
            _logger.LogWarning("RDP logon error {Error}.", errorCode);
        }
    }

    // ----------------------------- Quick controls -----------------------------

    private void OnSendCtrlAltDelClick(object sender, RoutedEventArgs e) => _rdpControl?.SendCtrlAltDel();

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (_control is null)
        {
            return;
        }

        if (System.Windows.MessageBox.Show(
                this,
                Text("RdpConnector.ConfirmRestartBody", "Restart the sandbox? The guest state is discarded."),
                Text("RdpConnector.Restart", "Restart"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunLifecycleAsync(async () =>
        {
            var runtime = await _control.RestartAsync(_request.SandboxId, _request.Mode, _closeCts.Token);
            _request = RdpSessionRequestFactory.Create(
                SandboxInstanceId.From(_request.SandboxId), runtime, _request.Mode, _request.RemoteAppProgram);
            await ReconnectAsync();
        }, Text("RdpConnector.Restarting", "Restarting…"));
    }

    private async void OnShutdownClick(object sender, RoutedEventArgs e)
    {
        if (_control is null)
        {
            return;
        }

        await RunLifecycleAsync(async () =>
        {
            await _control.ShutdownAsync(_request.SandboxId, _closeCts.Token);
            OnSandboxStopped();
        }, Text("RdpConnector.ShuttingDown", "Shutting down…"));
    }

    private async void OnForceShutdownClick(object sender, RoutedEventArgs e)
    {
        if (_control is null)
        {
            return;
        }

        if (System.Windows.MessageBox.Show(
                this,
                Text("RdpConnector.ConfirmForceShutdownBody", "Force the sandbox to power off?"),
                Text("RdpConnector.ForceShutdown", "Force shut down"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunLifecycleAsync(async () =>
        {
            await _control.ForceShutdownAsync(_request.SandboxId, _closeCts.Token);
            OnSandboxStopped();
        }, Text("RdpConnector.ForcingShutdown", "Forcing shut down…"));
    }

    private async void OnPowerOnClick(object sender, RoutedEventArgs e)
    {
        if (_control is null)
        {
            return;
        }

        await RunLifecycleAsync(async () =>
        {
            await _control.PowerOnAsync(_request.SandboxId, _request.Mode, _closeCts.Token);
            await ReconnectAsync();
        }, Text("RdpConnector.PoweringOn", "Powering on…"));
    }

    private void OnSandboxStopped()
    {
        Dispatcher.InvokeAsync(() =>
        {
            TearDownClient();
            SetStatus(Text("RdpConnector.Stopped", "Stopped"));
            ShowOverlay(
                Text("RdpConnector.StoppedTitle", "Sandbox stopped"),
                Text("RdpConnector.StoppedBody", "The sandbox has stopped. Use Power on to start it again."),
                string.Empty);
            UpdateToolbarState();
        });
    }

    private async Task RunLifecycleAsync(Func<Task> action, string busyText)
    {
        if (_busy)
        {
            return;
        }

        try
        {
            SetBusy(true, busyText);
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A sandbox lifecycle operation failed.");
            System.Windows.MessageBox.Show(this, ex.Message, Text("RdpConnector.LifecycleFailedTitle", "Operation failed"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    // ----------------------------- Full screen / resize -----------------------------

    private void OnToggleFullScreenClick(object sender, RoutedEventArgs e)
    {
        if (_fullScreen)
        {
            ExitFullScreen();
        }
        else
        {
            EnterFullScreen();
        }
    }

    private void EnterFullScreen()
    {
        if (_fullScreen)
        {
            return;
        }

        _previousState = WindowState;
        _previousStyle = WindowStyle;
        _previousResize = ResizeMode;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;

        _fullScreen = true;
        FullScreenButton.Content = Text("RdpConnector.ExitFullScreen", "Exit full screen");
    }

    private void ExitFullScreen()
    {
        if (!_fullScreen)
        {
            return;
        }

        WindowStyle = _previousStyle;
        ResizeMode = _previousResize;
        WindowState = _previousState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;

        _fullScreen = false;
        FullScreenButton.Content = Text("RdpConnector.FullScreen", "Full screen");
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            if (_fullScreen)
            {
                ExitFullScreen();
            }
            else
            {
                EnterFullScreen();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _fullScreen)
        {
            ExitFullScreen();
            e.Handled = true;
        }
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Coalesce the resize storm into one remote-desktop resize.
        _resizeTimer.Stop();
        _resizeTimer.Start();
    }

    private void OnResizeTimerTick(object? sender, EventArgs e)
    {
        _resizeTimer.Stop();
        _rdpControl?.UpdateRemoteDesktopSize();
    }

    private void OnOverlayCloseClick(object sender, RoutedEventArgs e) => Close();

    // ----------------------------- Helpers -----------------------------

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        CtrlAltDelButton.IsEnabled = !busy && _rdpControl?.IsConnected == true;
        RestartButton.IsEnabled = !busy && _control is not null;
        ShutdownButton.IsEnabled = !busy && _control is not null;
        ForceShutdownButton.IsEnabled = !busy && _control is not null;
        PowerOnButton.IsEnabled = !busy && _control is not null;
        FullScreenButton.IsEnabled = !busy;

        if (status is not null)
        {
            SetStatus(status);
        }
    }

    private void UpdateToolbarState()
    {
        if (_busy)
        {
            return;
        }

        CtrlAltDelButton.IsEnabled = _rdpControl?.IsConnected == true;
        var lifecycleEnabled = _control is not null;
        RestartButton.IsEnabled = lifecycleEnabled;
        ShutdownButton.IsEnabled = lifecycleEnabled;
        ForceShutdownButton.IsEnabled = lifecycleEnabled;
        PowerOnButton.IsEnabled = lifecycleEnabled;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void ShowOverlay(string title, string body, string detail)
    {
        OverlayTitle.Text = title;
        OverlayBody.Text = body;
        OverlayDetail.Text = detail;
        Overlay.Visibility = Visibility.Visible;
    }

    private void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

    private void TearDownClient()
    {
        var control = _rdpControl;
        _rdpControl = null;

        if (control is not null)
        {
            control.Connected -= OnSessionConnected;
            control.Disconnected -= OnSessionDisconnected;
            control.FatalError -= OnSessionFatalError;
            control.LogonError -= OnSessionLogonError;

            try
            {
                control.Disconnect();
            }
            catch
            {
                // Best effort.
            }

            try
            {
                control.Dispose();
            }
            catch
            {
                // Best effort.
            }

            RdpHost.Child = null;
        }

        try
        {
            _connector?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        _connector = null;
    }
}
