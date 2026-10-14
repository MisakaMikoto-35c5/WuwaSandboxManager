using System.Drawing;
using System.Windows.Forms;
using AxMSTSCLib;
using Microsoft.Extensions.Logging;
using MSTSCLib;
using Wsm.Connection.RdpBase;

namespace Wsm.Connection.RdpHost.Internal;

/// <summary>
/// WinForms window that hosts the strongly typed <c>mstscax</c> ActiveX control
/// (<see cref="AxMsRdpClient10NotSafeForScripting"/>) and connects it to an
/// RDPBASE named-pipe endpoint. It is created on a dedicated STA thread by
/// <see cref="WinFormsRdpSessionHost"/>; when the window closes the hosting task
/// completes.
/// <para>
/// The endpoint is acquired here, on the STA thread, while the WinForms message
/// loop pumps — mirroring the verified PersistentSandboxManager viewer and the
/// platform's own remote-session client. Acquiring it off the STA thread never
/// completes.
/// </para>
/// </summary>
internal sealed class RdpHostForm : Form
{
    private static readonly TimeSpan EndpointTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Process-wide gate around <c>IMsRdpClient10.Connect</c>. mstscax performs
    /// one-time, process-global initialization inside <c>Connect</c> (window
    /// class registration and shared state) without any synchronization: two
    /// controls connecting at the same moment in this process fail with
    /// <c>ERROR_CLASS_ALREADY_EXISTS</c> ("class already exists") or a bogus
    /// <c>OutOfMemoryException</c>. The sandbox host presents every session from
    /// this one process, so connects are serialized here.
    /// </summary>
    private static readonly SemaphoreSlim ConnectGate = new(1, 1);

    private readonly RdpSessionRequest _request;
    private readonly IRdpBaseConnectorFactory _connectorFactory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _closeCts = new();

    private AxMsRdpClient10NotSafeForScripting? _client;
    private IRdpBaseConnector? _connector;
    private Panel? _overlay;

    public RdpHostForm(RdpSessionRequest request, IRdpBaseConnectorFactory connectorFactory, ILogger logger)
    {
        _request = request;
        _connectorFactory = connectorFactory;
        _logger = logger;

        Text = string.IsNullOrWhiteSpace(request.WindowTitle) ? "Windows Sandbox" : request.WindowTitle;
        StartPosition = FormStartPosition.CenterScreen;
        Width = 1280;
        Height = 800;
        MinimumSize = new Size(640, 480);

        if (request.FullScreen)
        {
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
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
        _closeCts.Cancel();
        TearDown();
        base.OnFormClosed(e);
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
                    "Presenting {Endpoint} through mstscax over TCP.", EndpointLabel);
            }
            else
            {
                // Acquire the endpoint on this STA thread. The message loop pumps
                // while we await, which is what lets the RDPBASE callback complete.
                _connector = _connectorFactory.Create();
                endpoint = await _connector
                    .GetEndpointAsync(_request.PipeName, _closeCts.Token)
                    .WaitAsync(EndpointTimeout, _closeCts.Token)
                    .ConfigureAwait(true);

                if (IsDisposed || _closeCts.IsCancellationRequested)
                {
                    return;
                }

                _logger.LogInformation("Acquired RDPBASE endpoint {Endpoint}; presenting through mstscax.", _request.PipeName);
            }

            var client = new AxMsRdpClient10NotSafeForScripting { Dock = DockStyle.Fill };
            _client = client;
            Controls.Add(client);
            client.BringToFront();

            client.DesktopWidth = Math.Max(ClientSize.Width, MinimumSize.Width);
            client.DesktopHeight = Math.Max(ClientSize.Height, MinimumSize.Height);

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
            // session. (The platform's own client hosts a single sandbox per
            // process, so its constant id never collides there.)
            SetExtended(
                extended,
                "Workspace Id",
                _request.VmId != Guid.Empty ? $"in WindowsSandbox:{_request.VmId}" : "in WindowsSandbox");
            if (!useTcp)
            {
                // Container/pipe-only knobs. The HiDef container channel drives
                // full screen and the framebuffer and routes by container GUID;
                // over TCP there is no container channel at all, and setting
                // these crashes the control outright (SEHException from the
                // message loop right after Connect).
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

            if (_request.RemoteApp)
            {
                client.RemoteProgram3.RemoteProgramMode = true;

                // The WDAG guest is RemoteApp-only: it has no explorer desktop and
                // does not publish an application list, so the client must name the
                // program to start. Without it the guest tears the empty session
                // down and nothing appears.
                if (!string.IsNullOrWhiteSpace(_request.RemoteAppProgram))
                {
                    client.RemoteProgram3.RemoteApplicationProgram = _request.RemoteAppProgram;
                    _logger.LogInformation(
                        "RAIL session for {Endpoint} will launch {Program}.",
                        EndpointLabel, _request.RemoteAppProgram);
                }
            }

            client.OnConnected += (_, _) =>
            {
                _logger.LogInformation("RDP session connected for {Endpoint}.", EndpointLabel);
                RdpEventLog.Write("Connected", EndpointLabel);
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
                    RdpEventLog.Write("LogonError", $"code {args.lError} at {EndpointLabel}");
                }
            };
            client.OnDisconnected += (_, args) =>
            {
                _logger.LogInformation("RDP session disconnected (reason {Reason}) for {Endpoint}.", args.discReason, EndpointLabel);
                RdpEventLog.Write("Disconnected", $"reason {args.discReason} at {EndpointLabel}");
                ShowDisconnectedOverlay(
                    "Connection disconnected",
                    "The connection to the sandbox has been disconnected.",
                    DescribeDisconnect(args.discReason));
            };
            client.OnFatalError += (_, args) =>
            {
                _logger.LogError("RDP session fatal error {Error} for {Endpoint}.", args.errorCode, EndpointLabel);
                RdpEventLog.Write("FatalError", $"code {args.errorCode} at {EndpointLabel}");
                ShowDisconnectedOverlay(
                    "Connection lost",
                    "The connection to the sandbox was lost because of a fatal error.",
                    $"Error code {args.errorCode}.");
            };

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
            _logger.LogError(ex, "Unable to present the RDP session through mstscax; aborting.");
            Close();
        }
    }

    /// <summary>Human-readable session endpoint: the TCP address over TCP, else the pipe.</summary>
    private string EndpointLabel => !string.IsNullOrWhiteSpace(_request.ServerAddress)
        ? $"{_request.ServerAddress}:{_request.ServerPort}"
        : _request.PipeName;

    private static void SetExtended(IMsRdpExtendedSettings settings, string name, object value)
    {
        settings.set_Property(name, ref value);
    }

    /// <summary>
    /// Covers the (now blank) ActiveX surface with a readable notice that the
    /// session has ended. The window is intentionally left open so the user sees
    /// the message instead of a bare white client area.
    /// </summary>
    private void ShowDisconnectedOverlay(string title, string message, string detail)
    {
        if (IsDisposed || Disposing || _closeCts.IsCancellationRequested)
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

        // Hide the white ActiveX surface so the notice is the only thing visible.
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
            Font = new Font("Segoe UI", 16F, FontStyle.Bold),
            ForeColor = Color.White,
            Text = title,
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 12),
        };
        var body = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Font = new Font("Segoe UI", 11F),
            ForeColor = Color.White,
            Text = message,
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 6),
        };
        var reason = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(560, 0),
            Font = new Font("Segoe UI", 9.5F),
            ForeColor = Color.Gainsboro,
            Text = detail,
            TextAlign = ContentAlignment.MiddleCenter,
            Anchor = AnchorStyles.None,
            Margin = new Padding(0, 0, 0, 18),
        };
        var close = new Button
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(18, 4, 18, 4),
            Text = "Close",
            Anchor = AnchorStyles.None,
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
    /// Turns the raw <c>OnDisconnected</c> reason code into a short, human-readable
    /// sentence. Unknown codes fall back to a generic message with the code kept
    /// for diagnostics.
    /// </summary>
    private static string DescribeDisconnect(int discReason)
    {
        // Values are the documented IMsTscAxEvents::OnDisconnected disconnectReason
        // codes (Microsoft Learn: "IMsTscAxEvents OnDisconnected method"). The
        // low-value codes (0-3) are informational, not errors.
        var text = discReason switch
        {
            0 => "No additional information is available.",
            1 => "The local client ended the session.",
            2 => "The sandbox ended the session (remote disconnection by user).",
            3 => "The sandbox server ended the session.",
            260 => "The sandbox address could not be resolved (DNS lookup failed).",
            262 => "The client ran out of memory.",
            264 => "The connection attempt timed out.",
            516 => "The connection to the sandbox could not be established (socket connect failed).",
            518 => "The client ran out of memory.",
            774 => "The client ran out of memory.",
            1028 => "The connection to the sandbox was lost (socket receive failed).",
            1288 => "The sandbox address could not be resolved (DNS lookup failed).",
            1798 => "The server certificate could not be unpacked.",
            2056 => "Licensing negotiation failed.",
            2312 => "Licensing timed out.",
            3078 => "The session ended because of a decryption error.",
            3080 => "The session ended because of a decompression error.",
            _ => "The session ended unexpectedly.",
        };

        return $"Reason: {text} (code {discReason}).";
    }

    private void TearDown()
    {
        var client = _client;
        _client = null;

        if (client is not null)
        {
            // Disconnect explicitly before releasing the ActiveX host. Merely
            // disposing the control can leave the guest session and the RDPBASE
            // named-pipe connector held, so a later connect is refused ("the
            // session is occupied"). A graceful Disconnect lets the guest end the
            // session cleanly.
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
}
