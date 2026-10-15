using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AxMSTSCLib;
using Microsoft.Extensions.Logging;
using MSTSCLib;
using Wsm.Connection.RdpBase;

namespace Wsm.Connection.RailClient.Internal;

/// <summary>
/// The WDAG-style RemoteApp client window. A single borderless dialog hosts the
/// strongly typed <c>mstscax</c> control over an RDPBASE named-pipe endpoint (or
/// TCP), with <c>RemoteProgram3.RemoteProgramMode</c> enabled — the same shape as
/// WDAG's <c>hvsirdpclient.exe</c> / the platform's remote-session client.
/// <para>
/// Presentation: while the session starts, the dialog (with the control's own
/// "starting your application" surface) is shown; as soon as the first remote
/// application window appears the dialog is hidden with an <b>empty window
/// region</b> (<c>SetWindowRgn</c>) instead of <c>ShowWindow(SW_HIDE)</c>, so the
/// application windows — which the control creates as free-floating top-level
/// windows — keep rendering and behave like local windows (drag, stack,
/// minimize). On a lost session the dialog becomes visible again with the
/// reason.
/// </para>
/// </summary>
internal sealed class RailClientForm : Form
{
    private static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Process-wide gate around <c>IMsRdpClient10.Connect</c>. mstscax performs
    /// one-time, process-global initialization inside <c>Connect</c> without any
    /// synchronization; two controls connecting at the same moment in this
    /// process fail with <c>ERROR_CLASS_ALREADY_EXISTS</c> or a bogus
    /// <c>OutOfMemoryException</c>. One client process hosts one RAIL session, so
    /// this is only a safety net.
    /// </summary>
    private static readonly SemaphoreSlim ConnectGate = new(1, 1);

    private const int RemoteWindowPollMilliseconds = 500;

    /// <summary>
    /// The session shell a RemoteApp connection requests. It is the
    /// <c>alternate shell:s:rdpinit.exe</c> of a published-application
    /// <c>.rdp</c>: the guest's "RemoteApp Logon Application" that serves the
    /// RAIL channel and starts the requested program.
    /// </summary>
    private const string RemoteAppShell = "rdpinit.exe";

    private readonly RailClientSessionRequest _request;
    private readonly IRdpBaseConnectorFactory _connectorFactory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _closeCts = new();
    private readonly System.Windows.Forms.Timer _remoteWindowTimer = new()
    {
        Interval = RemoteWindowPollMilliseconds,
    };

    private AxMsRdpClient10NotSafeForScripting? _client;
    private IRdpBaseConnector? _connector;
    private Panel? _overlay;
    private bool _sessionConnected;
    private bool _dialogDismissed;
    private bool _closing;

    public RailClientForm(
        RailClientSessionRequest request,
        IRdpBaseConnectorFactory connectorFactory,
        ILogger logger)
    {
        _request = request;
        _connectorFactory = connectorFactory;
        _logger = logger;

        Text = string.IsNullOrWhiteSpace(request.WindowTitle) ? "Windows Sandbox" : request.WindowTitle;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(24, 24, 28);
        ClientSize = new Size(420, 240);
        MinimumSize = Size;
        MaximumSize = Size;
    }

    /// <summary>
    /// Set when the session could not be presented. The hosting thread faults the
    /// completion task with it so the caller can surface the failure instead of
    /// silently showing nothing.
    /// </summary>
    public Exception? Failure { get; private set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = ConnectAsync();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true;
        _closeCts.Cancel();
        _remoteWindowTimer.Stop();
        TearDown();
        base.OnFormClosed(e);
    }

    /// <summary>Lets the user drag the borderless dialog by its surface.</summary>
    protected override void WndProc(ref Message m)
    {
        const int WmNcHitTest = 0x84;
        const int HtClient = 1;
        const int HtCaption = 2;

        base.WndProc(ref m);
        if (m.Msg == WmNcHitTest && m.Result.ToInt32() == HtClient)
        {
            m.Result = HtCaption;
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            var useTcp = !string.IsNullOrWhiteSpace(_request.ServerAddress);
            object? endpoint = null;

            if (useTcp)
            {
                _logger.LogInformation(
                    "Starting the RemoteApp client for {Endpoint} over TCP.", EndpointLabel);
            }
            else
            {
                // Acquire the endpoint on this STA thread. The message loop pumps
                // while we await, which is what lets the RDPBASE callback complete.
                _connector = _connectorFactory.Create();
                endpoint = await _connector
                    .GetEndpointAsync(_request.PipeName!, _closeCts.Token)
                    .WaitAsync(EndpointTimeout, _closeCts.Token)
                    .ConfigureAwait(true);

                if (IsDisposed || _closeCts.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogInformation(
                    "Acquired RDPBASE endpoint {Endpoint}; starting the RemoteApp client.", _request.PipeName);
            }

            var client = new AxMsRdpClient10NotSafeForScripting { Dock = DockStyle.Fill };
            _client = client;
            Controls.Add(client);
            client.BringToFront();

            client.DesktopWidth = Math.Max(ClientSize.Width, 800);
            client.DesktopHeight = Math.Max(ClientSize.Height, 600);

            // Mirror the platform's own remote-session client: NLA is not used,
            // the security layer is negotiated, server authentication is not
            // required, and the credential is supplied in clear text.
            var advanced = client.AdvancedSettings9;
            advanced.EnableCredSspSupport = false;
            advanced.NegotiateSecurityLayer = true;
            advanced.AuthenticationLevel = 0;

            advanced.DisableCtrlAltDel = 1;
            advanced.DisplayConnectionBar = true;
            advanced.RedirectClipboard = _request.Redirections.Clipboard;
            advanced.RedirectPrinters = _request.Redirections.Printer;
            advanced.RedirectSmartCards = _request.Redirections.SmartCard;
            advanced.RedirectDevices = false;
            advanced.RedirectDrives = false;
            advanced.RedirectDirectX = false;
            advanced.RedirectPorts = false;
            advanced.RedirectPOSDevices = false;
            advanced.SmartSizing = true;
            advanced.keepAliveInterval = 10000;
            advanced.MinutesToIdleTimeout = 0;
            advanced.overallConnectionTimeout = 120;
            advanced.PerformanceFlags = 132;
            advanced.NetworkConnectionType = 6u;
            advanced.EnableAutoReconnect = true;
            advanced.MaxReconnectAttempts = 5;
            advanced.RelativeMouseMode = true;
            advanced.VideoPlaybackMode = 0u;

            client.UserName = _request.UserName;
            advanced.ClearTextPassword = _request.Password;

            var extended = (IMsRdpExtendedSettings)client.GetOcx()!;
            // The workspace id scopes the client's per-process session tracking;
            // it must differ per sandbox, or connections made from this process
            // are treated as one workspace and a new connect bumps the previous
            // session.
            SetExtended(
                extended,
                "Workspace Id",
                _request.VmId != Guid.Empty ? $"in WindowsSandbox:{_request.VmId}" : "in WindowsSandbox");
            if (!useTcp)
            {
                // Container/pipe-only knobs. The HiDef container channel drives
                // full screen and the framebuffer and routes by container GUID;
                // over TCP there is no container channel at all, and setting
                // these crashes the control outright.
                advanced.ContainerHandledFullScreen = 1;
                SetExtended(extended, "ClientDeviceName", _request.VmId.ToString());
                SetExtended(extended, "EnableHardwareMode", true);
                SetExtended(extended, "EnableFrameBufferRedirection", true);
                if (_request.VmId != Guid.Empty)
                {
                    SetExtended(extended, "HiDefRemoteAppContainerGUID", $"{{{_request.VmId}}}");
                }
            }
            SetExtended(extended, "AllowSasSequenceTransmission", false);
            SetExtended(extended, "DisableAutoReconnectComponent", true);

            // The RemoteApp (RAIL) bootstrap, mirroring what a published-application
            // .rdp asks for (remoteapptool's field set):
            //   * RemoteProgramMode      <-> remoteapplicationmode:i:1
            //   * RemoteApplicationProgram  <-> remoteapplicationprogram:s:<path>
            //   * StartProgram=rdpinit.exe  <-> alternate shell:s:rdpinit.exe
            // The guest session's shell becomes rdpinit.exe (the "RemoteApp Logon
            // Application"), which serves the RAIL channel and launches the program
            // the client requests with the remoteapplicationprogram. Without the
            // rdpinit shell the guest runs a normal desktop session and the RAIL
            // exec order has no server-side listener.
            //
            // WSM_RAIL_SHELL / WSM_RAIL_APP are temporary experiment knobs used to
            // isolate the guest's acceptance conditions:
            //   WSM_RAIL_SHELL = the session shell ("" disables the alternate shell)
            //   WSM_RAIL_APP   = connect|start|both|none (how the program is requested)
            client.RemoteProgram3.RemoteProgramMode = true;
            client.RemoteProgram3.RemoteApplicationName = _request.ApplicationName;

            var appMode = (Environment.GetEnvironmentVariable("WSM_RAIL_APP") ?? "connect").Trim().ToLowerInvariant();
            if (appMode is "connect" or "both")
            {
                client.RemoteProgram3.RemoteApplicationProgram = _request.Program;
                if (!string.IsNullOrWhiteSpace(_request.ProgramArguments))
                {
                    client.RemoteProgram3.RemoteApplicationArgs = _request.ProgramArguments!;
                }
            }

            var shell = Environment.GetEnvironmentVariable("WSM_RAIL_SHELL") ?? RemoteAppShell;
            if (string.Equals(shell.Trim(), "none", StringComparison.OrdinalIgnoreCase))
            {
                shell = string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(shell))
            {
                try
                {
                    client.SecuredSettings3.StartProgram = shell.Trim();
                    _logger.LogInformation("RemoteApp session shell: {Shell}.", shell.Trim());
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Setting the RemoteApp session shell failed.");
                }
            }

            client.OnLoginComplete += (_, _) =>
            {
                _logger.LogInformation("RemoteApp logon completed for {Endpoint} ({Program}).", EndpointLabel, _request.Program);
                ConnectionEventLog.Write("LogonComplete", $"{EndpointLabel} (RAIL {_request.Program})");

                if (appMode is "start" or "both")
                {
                    try
                    {
                        client.RemoteProgram3.ServerStartProgram(
                            _request.Program,
                            string.Empty,
                            string.Empty,
                            false,
                            _request.ProgramArguments ?? string.Empty,
                            false);
                        _logger.LogInformation("ServerStartProgram requested for {Program}.", _request.Program);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "ServerStartProgram failed for {Endpoint}.", EndpointLabel);
                    }
                }
            };
            client.OnRemoteProgramResult += (_, args) =>
            {
                // The guest's answer to the start request: lError is the
                // RemoteProgramResult code (ok, not-in-whitelist, file-not-found, …).
                var succeeded = args.lError == RemoteProgramResult.remoteAppResultOk;
                if (succeeded)
                {
                    _logger.LogInformation("RemoteApp program {Program} accepted by {Endpoint}.", _request.Program, EndpointLabel);
                }
                else
                {
                    _logger.LogWarning(
                        "RemoteApp program {Program} failed on {Endpoint} (result {Result}, executable={IsExecutable}).",
                        _request.Program, EndpointLabel, args.lError, args.vbIsExecutable);
                }

                ConnectionEventLog.Write(
                    "RemoteProgramResult",
                    $"{EndpointLabel} program={args.bstrRemoteProgram} result={args.lError} executable={args.vbIsExecutable}");
            };
            client.OnRemoteProgramDisplayed += (_, args) =>
            {
                // The remote program's first window is on screen: the status
                // dialog's job is done (the platform client dismisses its
                // internal dialog on exactly this signal).
                ConnectionEventLog.Write("RemoteProgramDisplayed", $"{EndpointLabel} displayed={args.vbDisplayed}");
                if (args.vbDisplayed)
                {
                    DismissClientDialog();
                }
            };
            client.OnRemoteWindowDisplayed += (_, args) =>
            {
                ConnectionEventLog.Write(
                    "RemoteWindowDisplayed",
                    $"{EndpointLabel} displayed={args.vbDisplayed} attribute={args.windowAttribute}");
                if (args.vbDisplayed)
                {
                    DismissClientDialog();
                }
            };

            client.OnConnected += (_, _) =>
            {
                _sessionConnected = true;
                _logger.LogInformation("RemoteApp session connected for {Endpoint}.", EndpointLabel);
                ConnectionEventLog.Write("Connected", $"{EndpointLabel} (RAIL {_request.Program})");
                _remoteWindowTimer.Start();
            };
            client.OnLogonError += (_, args) =>
            {
                // Arbitration codes (-2..-7) are logon progress events, not
                // errors: -2 (ARBITRATION_CODE_CONTINUE_LOGON) fires on every
                // normal logon. Only genuine failures deserve a warning.
                if (args.lError is >= -7 and <= -2)
                {
                    _logger.LogInformation("RDP logon arbitration code {Code} for {Endpoint}.", args.lError, EndpointLabel);
                }
                else
                {
                    _logger.LogWarning("RDP logon error {Error} for {Endpoint}.", args.lError, EndpointLabel);
                    ConnectionEventLog.Write("LogonError", $"code {args.lError} at {EndpointLabel}");
                }
            };
            client.OnDisconnected += (_, args) =>
            {
                var description = DescribeDisconnect(args.discReason, client);
                _logger.LogInformation("RemoteApp session disconnected (reason {Reason}) for {Endpoint}: {Description}", args.discReason, EndpointLabel, description);
                ConnectionEventLog.Write("Disconnected", $"reason {args.discReason} at {EndpointLabel} (RAIL) - {description}");

                if (args.discReason is 2 or 3 && !_closing)
                {
                    // The remote side ended the session — the application closed.
                    // The client's job is done, so the process exits like the
                    // platform client does.
                    Close();
                    return;
                }

                ShowDisconnectedOverlay(
                    "Connection disconnected",
                    "The connection to the sandbox has been disconnected.",
                    $"Reason: {description} (code {args.discReason}).");
            };
            client.OnFatalError += (_, args) =>
            {
                _logger.LogError("RemoteApp session fatal error {Error} for {Endpoint}.", args.errorCode, EndpointLabel);
                ConnectionEventLog.Write("FatalError", $"code {args.errorCode} at {EndpointLabel} (RAIL)");
                ShowDisconnectedOverlay(
                    "Connection lost",
                    "The connection to the sandbox was lost because of a fatal error.",
                    $"Error code {args.errorCode}.");
            };

            _remoteWindowTimer.Tick += (_, _) => WatchForRemoteWindows();

            if (useTcp)
            {
                client.Server = _request.ServerAddress!;
                advanced.RDPPort = _request.ServerPort;
            }
            else
            {
                var endpointObject = endpoint!;
                client.AdvancedSettings9.set_ConnectWithEndpoint(ref endpointObject);
            }

            // Serialized: mstscax is not safe for concurrent Connect calls
            // within one process (see ConnectGate).
            await ConnectGate.WaitAsync(_closeCts.Token).ConfigureAwait(true);
            try
            {
                client.Connect();
            }
            finally
            {
                ConnectGate.Release();
            }
        }
        catch (Exception ex)
        {
            Failure = ex;
            _logger.LogError(ex, "Unable to present the RemoteApp session through mstscax; aborting.");
            Close();
        }
    }

    /// <summary>
    /// The remote application windows are created by the control as top-level
    /// windows of this process. The first one that appears means the session is
    /// up and the application is on screen, so the status dialog gets out of the
    /// way (an empty window region keeps the window hierarchy "shown" so the
    /// floating windows keep rendering).
    /// </summary>
    private void WatchForRemoteWindows()
    {
        if (!_sessionConnected || _dialogDismissed || _closing || IsDisposed || Disposing)
        {
            return;
        }

        if (!HasRemoteWindows())
        {
            return;
        }

        _remoteWindowTimer.Stop();
        DismissClientDialog();
    }

    /// <summary>
    /// True when this process owns a visible, captioned top-level window other
    /// than the client dialog itself: that is a remote application window (the
    /// control's own connection bar is a tool window and is skipped).
    /// </summary>
    private bool HasRemoteWindows()
    {
        const long WsExToolWindow = 0x00000080;

        var processId = Environment.ProcessId;
        var formHandle = Handle;
        var found = false;

        EnumWindows(
            (handle, _) =>
            {
                if (handle == formHandle)
                {
                    return true;
                }

                GetWindowThreadProcessId(handle, out var windowProcessId);
                if (windowProcessId != (uint)processId || !IsWindowVisible(handle))
                {
                    return true;
                }

                if (GetWindowTextLength(handle) == 0)
                {
                    // The control's canvas helpers are untitled.
                    return true;
                }

                var extendedStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
                if ((extendedStyle & WsExToolWindow) != 0)
                {
                    // The connection bar (and similar chrome) is a tool window.
                    return true;
                }

                found = true;
                return false;
            },
            IntPtr.Zero);

        return found;
    }

    private void DismissClientDialog()
    {
        if (_dialogDismissed || IsDisposed || Disposing)
        {
            return;
        }

        _dialogDismissed = true;
        try
        {
            TopMost = false;
        }
        catch
        {
            // Best effort.
        }

        // Shape the dialog to an empty region — the platform client's technique
        // (SetWindowRgn) rather than SW_HIDE, so the window chain stays visible
        // for the remote application windows.
        var region = CreateRectRgn(0, 0, 0, 0);
        if (SetWindowRgn(Handle, region, true) == 0 && region != IntPtr.Zero)
        {
            // The system owns the region only on success.
            DeleteObject(region);
        }

        ConnectionEventLog.Write(
            "RemoteWindowDisplayed",
            $"sandbox={_request.SandboxId:D} program={_request.Program} for {EndpointLabel}");
        _logger.LogInformation(
            "Remote application window displayed; the client status dialog is now hidden (sandbox {SandboxId}).",
            _request.SandboxId);
    }

    /// <summary>Human-readable session endpoint: the TCP address over TCP, else the pipe.</summary>
    private string EndpointLabel => !string.IsNullOrWhiteSpace(_request.ServerAddress)
        ? $"{_request.ServerAddress}:{_request.ServerPort}"
        : _request.PipeName ?? "(no endpoint)";

    private static void SetExtended(IMsRdpExtendedSettings settings, string name, object value)
    {
        settings.set_Property(name, ref value);
    }

    /// <summary>
    /// Makes the dialog visible again and covers the client area with a readable
    /// notice that the session has ended. The window is intentionally left open
    /// so the user sees the message instead of nothing.
    /// </summary>
    private void ShowDisconnectedOverlay(string title, string message, string detail)
    {
        if (IsDisposed || Disposing || _closing || _closeCts.IsCancellationRequested)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => ShowDisconnectedOverlay(title, message, detail)));
            }
            catch (InvalidOperationException)
            {
                // The window is being torn down; there is nothing to show.
            }

            return;
        }

        if (_overlay is not null)
        {
            return;
        }

        // The dialog may have been region-hidden when the session started.
        SetWindowRgn(Handle, IntPtr.Zero, true);
        try
        {
            TopMost = true;
        }
        catch
        {
            // Best effort.
        }

        try
        {
            _client?.Hide();
        }
        catch
        {
            // Best effort.
        }

        var content = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var heading = new Label
        {
            AutoSize = true,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Color.White,
            Text = title,
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 10),
        };
        var body = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = Color.White,
            Text = message,
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 4),
        };
        var reason = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Font = new Font("Segoe UI", 8.5F),
            ForeColor = Color.Gainsboro,
            Text = detail,
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 14),
        };
        var close = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(16, 3, 16, 3),
            Text = "Close",
            Anchor = AnchorStyles.None,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
        };
        close.Click += (_, _) => Close();

        content.Controls.Add(heading);
        content.Controls.Add(body);
        content.Controls.Add(reason);
        content.Controls.Add(close);

        var overlay = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(24, 24, 28),
        };
        overlay.Controls.Add(content);

        void CenterContent()
        {
            content.Location = new Point(
                Math.Max(0, (overlay.ClientSize.Width - content.Width) / 2),
                Math.Max(0, (overlay.ClientSize.Height - content.Height) / 2));
        }

        overlay.Resize += (_, _) => CenterContent();
        content.SizeChanged += (_, _) => CenterContent();

        _overlay = overlay;
        Controls.Add(overlay);
        overlay.BringToFront();
        CenterContent();
        ActiveControl = close;
    }

    /// <summary>
    /// The control's own error description for a disconnect reason (falls back
    /// to a generic message when the control cannot describe the code).
    /// </summary>
    private static string DescribeDisconnect(int discReason, AxMsRdpClient10NotSafeForScripting client)
    {
        try
        {
            var client10 = (IMsRdpClient10)client.GetOcx()!;
            var description = client10.GetErrorDescription((uint)discReason, (uint)client10.ExtendedDisconnectReason);
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }
        }
        catch
        {
            // The control may be gone; fall through.
        }

        return "The session ended unexpectedly.";
    }

    private void TearDown()
    {
        var client = _client;
        _client = null;

        if (client is not null)
        {
            // Disconnect explicitly before releasing the ActiveX host. Merely
            // disposing the control can leave the guest session and the RDPBASE
            // named-pipe connector held, so a later connect is refused.
            try
            {
                if (client.Connected != 0)
                {
                    client.Disconnect();
                }
            }
            catch
            {
                // Best effort: the control may already be gone.
            }

            try
            {
                client.Dispose();
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

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    private const int GwlExStyle = -20;

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr window, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
