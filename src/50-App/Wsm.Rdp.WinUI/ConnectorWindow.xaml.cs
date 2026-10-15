using System.Runtime.InteropServices;
using Microsoft.Extensions.Localization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wsm.Connection.RdpBase;
using Wsm.Connection.RdpHost;
using Wsm.Connection.RdpHost.Ax;
using Wsm.Localization;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace Wsm.Rdp.WinUI;

/// <summary>
/// The WinUI 3 RDP connector window. Its chrome (toolbar with the quick
/// lifecycle controls and full-screen toggle, and the disconnected overlay) is
/// WinUI 3; the RDP surface is the shared <c>mstscax</c> ActiveX control hosted
/// as a child HWND parented over the <c>Surface</c> region and repositioned on
/// every layout/resize.
/// </summary>
public sealed partial class ConnectorWindow : Window
{
    private static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(30);

    private readonly RdpSessionRequest _request;
    private readonly IRdpSandboxControl? _control;
    private readonly IRdpBaseConnectorFactory _connectorFactory;
    private readonly IStringLocalizer<WindowsSandboxManagerResource> _l;
    private readonly CancellationTokenSource _closeCts = new();

    private RdpClientControl? _activeX;
    private IRdpBaseConnector? _connector;
    private nint _activeXHandle;
    private bool _started;
    private bool _busy;
    private bool _fullScreen;
    private RdpSessionRequest _current;

    public ConnectorWindow(
        RdpSessionRequest request,
        IRdpSandboxControl? control,
        IRdpBaseConnectorFactory connectorFactory,
        IStringLocalizer<WindowsSandboxManagerResource> localizer)
    {
        _request = request;
        _current = request;
        _control = control;
        _connectorFactory = connectorFactory;
        _l = localizer;

        InitializeComponent();

        Title = string.IsNullOrWhiteSpace(request.WindowTitle) ? "Windows Sandbox" : request.WindowTitle;
        try
        {
            AppWindow.Resize(new SizeInt32(1280, 800));
        }
        catch
        {
            // Sizing is cosmetic.
        }

        ApplyLocalization();

        Surface.SizeChanged += (_, _) => PositionActiveX();
        Root.LayoutUpdated += (_, _) => PositionActiveX();
        Activated += OnFirstActivated;
        Closed += OnClosed;
    }

    // ----------------------------- Startup -----------------------------

    private async void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        Activated -= OnFirstActivated;

        StartActiveX();
        await ConnectAsync();
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

    // ----------------------------- ActiveX hosting -----------------------------

    private void StartActiveX()
    {
        var host = WindowNative.GetWindowHandle(this);
        var activeX = new RdpClientControl();
        activeX.Connected += OnSessionConnected;
        activeX.Disconnected += OnSessionDisconnected;
        activeX.FatalError += OnSessionFatalError;
        activeX.LogonError += OnSessionLogonError;

        _activeX = activeX;
        _activeXHandle = activeX.Handle;

        // Reparent the WinForms control's window under the WinUI window and give
        // it child styles so it is laid out inside the Surface region.
        var style = NativeMethods.GetWindowLong(_activeXHandle, NativeMethods.GwlStyle);
        style = (style & ~NativeMethods.WsPopup) | NativeMethods.WsChild;
        NativeMethods.SetWindowLong(_activeXHandle, NativeMethods.GwlStyle, style);
        NativeMethods.SetParent(_activeXHandle, host);
        NativeMethods.ShowWindow(_activeXHandle, NativeMethods.SwShow);
        PositionActiveX();
    }

    private void PositionActiveX()
    {
        if (_activeXHandle == 0 || Root.XamlRoot is null)
        {
            return;
        }

        try
        {
            var bounds = Surface
                .TransformToVisual(Root)
                .TransformBounds(new Rect(0, 0, Surface.ActualWidth, Surface.ActualHeight));
            var scale = Root.XamlRoot.RasterizationScale;

            NativeMethods.SetWindowPos(
                _activeXHandle,
                nint.Zero,
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                Math.Max(1, (int)Math.Round(bounds.Width * scale)),
                Math.Max(1, (int)Math.Round(bounds.Height * scale)),
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);

            _activeX?.UpdateRemoteDesktopSize();
        }
        catch
        {
            // Lay-out runs frequently; a transient failure is not fatal.
        }
    }

    // ----------------------------- Connect -----------------------------

    private async Task ConnectAsync()
    {
        if (_activeX is null)
        {
            return;
        }

        try
        {
            SetStatus(Text("RdpConnector.Connecting", "Connecting…"));
            await _activeX.ConnectAsync(_current, AcquireEndpointAsync, _closeCts.Token);
            SetStatus(Text("RdpConnector.Connected", "Connected"));
            UpdateToolbarState();
        }
        catch (Exception ex)
        {
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
            .GetEndpointAsync(_current.PipeName, cancellationToken)
            .WaitAsync(EndpointTimeout, cancellationToken)
            .ConfigureAwait(true);
    }

    private async Task ReconnectAsync()
    {
        TearDownActiveX();
        HideOverlay();
        StartActiveX();
        await ConnectAsync();
    }

    // ----------------------------- Session events -----------------------------

    private void OnSessionConnected(object? sender, EventArgs e)
    {
        SetStatus(Text("RdpConnector.Connected", "Connected"));
        UpdateToolbarState();
        PositionActiveX();
    }

    private void OnSessionDisconnected(object? sender, RdpDisconnectEventArgs e)
    {
        UpdateToolbarState();
        ShowOverlay(
            Text("RdpConnector.DisconnectedTitle", "Connection disconnected"),
            Text("RdpConnector.DisconnectedBody", "The connection to the sandbox has been disconnected."),
            $"Reason: {e.ReasonText} (code {e.Reason}).");
    }

    private void OnSessionFatalError(object? sender, int errorCode)
    {
        UpdateToolbarState();
        ShowOverlay(
            Text("RdpConnector.LostTitle", "Connection lost"),
            Text("RdpConnector.LostBody", "The connection to the sandbox was lost because of a fatal error."),
            $"Error code {errorCode}.");
    }

    private void OnSessionLogonError(object? sender, int errorCode)
    {
        // Arbitration codes (-2..-7) are logon progress events, not errors.
    }

    // ----------------------------- Quick controls -----------------------------

    private void OnCtrlAltDelClick(object sender, RoutedEventArgs e) => _activeX?.SendCtrlAltDel();

    private async void OnRestartClick(object sender, RoutedEventArgs e)
    {
        if (_control is null ||
            !await ConfirmAsync(
                Text("RdpConnector.Restart", "Restart"),
                Text("RdpConnector.ConfirmRestartBody", "Restart the sandbox? The guest state is discarded.")))
        {
            return;
        }

        await RunLifecycleAsync(async () =>
        {
            var runtime = await _control.RestartAsync(_current.SandboxId, _current.Mode, _closeCts.Token);
            _current = RdpSessionRequestFactory.Create(
                Wsm.Domain.ValueObjects.SandboxInstanceId.From(_current.SandboxId),
                runtime, _current.Mode, _current.RemoteAppProgram);
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
            await _control.ShutdownAsync(_current.SandboxId, _closeCts.Token);
            OnSandboxStopped();
        }, Text("RdpConnector.ShuttingDown", "Shutting down…"));
    }

    private async void OnForceShutdownClick(object sender, RoutedEventArgs e)
    {
        if (_control is null ||
            !await ConfirmAsync(
                Text("RdpConnector.ForceShutdown", "Force shut down"),
                Text("RdpConnector.ConfirmForceShutdownBody", "Force the sandbox to power off?")))
        {
            return;
        }

        await RunLifecycleAsync(async () =>
        {
            await _control.ForceShutdownAsync(_current.SandboxId, _closeCts.Token);
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
            await _control.PowerOnAsync(_current.SandboxId, _current.Mode, _closeCts.Token);
            await ReconnectAsync();
        }, Text("RdpConnector.PoweringOn", "Powering on…"));
    }

    private void OnSandboxStopped()
    {
        TearDownActiveX();
        SetStatus(Text("RdpConnector.Stopped", "Stopped"));
        ShowOverlay(
            Text("RdpConnector.StoppedTitle", "Sandbox stopped"),
            Text("RdpConnector.StoppedBody", "The sandbox has stopped. Use Power on to start it again."),
            string.Empty);
        UpdateToolbarState();
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
            await ConfirmAsync(Text("RdpConnector.LifecycleFailedTitle", "Operation failed"), ex.Message);
        }
        finally
        {
            SetBusy(false, null);
        }
    }

    // ----------------------------- Dialogs -----------------------------

    private async Task<bool> ConfirmAsync(string title, string body)
    {
        // The ActiveX child HWND always paints over XAML (airspace), so hide it
        // while a dialog is on screen.
        SetActiveXVisible(false);
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = body,
                PrimaryButtonText = "OK",
                CloseButtonText = Text("RdpConnector.Close", "Close"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Root.XamlRoot,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch
        {
            return false;
        }
        finally
        {
            SetActiveXVisible(true);
        }
    }

    private void SetActiveXVisible(bool visible)
    {
        if (_activeXHandle == 0)
        {
            return;
        }

        NativeMethods.ShowWindow(_activeXHandle, visible ? NativeMethods.SwShow : NativeMethods.SwHide);
        if (visible)
        {
            PositionActiveX();
        }
    }

    // ----------------------------- Full screen -----------------------------

    private void OnToggleFullScreenClick(object sender, RoutedEventArgs e)
    {
        if (_fullScreen)
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            _fullScreen = false;
            FullScreenButton.Content = Text("RdpConnector.FullScreen", "Full screen");
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            _fullScreen = true;
            FullScreenButton.Content = Text("RdpConnector.ExitFullScreen", "Exit full screen");
        }

        PositionActiveX();
    }

    private void OnOverlayCloseClick(object sender, RoutedEventArgs e) => Close();

    // ----------------------------- Helpers -----------------------------

    private void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        CtrlAltDelButton.IsEnabled = !busy && _activeX?.IsConnected == true;
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

        CtrlAltDelButton.IsEnabled = _activeX?.IsConnected == true;
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
        SetActiveXVisible(false);
    }

    private void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closeCts.Cancel();
        TearDownActiveX();
    }

    private void TearDownActiveX()
    {
        var activeX = _activeX;
        _activeX = null;
        _activeXHandle = nint.Zero;

        if (activeX is not null)
        {
            activeX.Connected -= OnSessionConnected;
            activeX.Disconnected -= OnSessionDisconnected;
            activeX.FatalError -= OnSessionFatalError;
            activeX.LogonError -= OnSessionLogonError;

            try
            {
                activeX.Disconnect();
            }
            catch
            {
                // Best effort.
            }

            try
            {
                activeX.Dispose();
            }
            catch
            {
                // Best effort.
            }
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
