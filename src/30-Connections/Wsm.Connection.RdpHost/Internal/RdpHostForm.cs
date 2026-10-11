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

    private readonly RdpSessionRequest _request;
    private readonly IRdpBaseConnectorFactory _connectorFactory;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _closeCts = new();

    private AxMsRdpClient10NotSafeForScripting? _client;
    private IRdpBaseConnector? _connector;

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
            // Acquire the endpoint on this STA thread. The message loop pumps
            // while we await, which is what lets the RDPBASE callback complete.
            _connector = _connectorFactory.Create();
            var endpoint = await _connector
                .GetEndpointAsync(_request.PipeName, _closeCts.Token)
                .WaitAsync(EndpointTimeout, _closeCts.Token)
                .ConfigureAwait(true);

            if (IsDisposed || _closeCts.IsCancellationRequested)
            {
                return;
            }

            _logger.LogInformation("Acquired RDPBASE endpoint {Endpoint}; presenting through mstscax.", _request.PipeName);

            var client = new AxMsRdpClient10NotSafeForScripting { Dock = DockStyle.Fill };
            _client = client;
            Controls.Add(client);
            client.BringToFront();

            client.DesktopWidth = Math.Max(ClientSize.Width, MinimumSize.Width);
            client.DesktopHeight = Math.Max(ClientSize.Height, MinimumSize.Height);

            // Mirror the platform's own remote-session client (the decompiled
            // WindowsSandboxServer / WindowsSandboxRemoteSession) for the RDPBASE
            // named-pipe transport, exactly. NLA is not used, the security layer
            // is negotiated, server authentication is not required, and the
            // credential is supplied in clear text (local pipe trust domain only).
            var advanced = client.AdvancedSettings9;
            advanced.EnableCredSspSupport = false;
            advanced.NegotiateSecurityLayer = true;
            advanced.AuthenticationLevel = 0;
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
            SetExtended(extended, "Workspace Id", "in WindowsSandbox");
            SetExtended(extended, "ClientDeviceName", _request.VmId.ToString());
            SetExtended(extended, "EnableHardwareMode", true);
            SetExtended(extended, "EnableFrameBufferRedirection", true);
            SetExtended(extended, "AllowSasSequenceTransmission", false);
            SetExtended(extended, "DisableAutoReconnectComponent", true);
            if (_request.VmId != Guid.Empty)
            {
                SetExtended(extended, "HiDefRemoteAppContainerGUID", $"{{{_request.VmId}}}");
            }

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
                        _request.PipeName, _request.RemoteAppProgram);
                }
            }

            client.OnConnected += (_, _) => _logger.LogInformation("RDP session connected for {Endpoint}.", _request.PipeName);
            client.OnLogonError += (_, args) => _logger.LogWarning("RDP logon error {Error} for {Endpoint}.", args.lError, _request.PipeName);
            client.OnDisconnected += (_, args) => _logger.LogInformation("RDP session disconnected (reason {Reason}) for {Endpoint}.", args.discReason, _request.PipeName);

            client.AdvancedSettings9.set_ConnectWithEndpoint(ref endpoint);
            client.Connect();
        }
        catch (Exception ex)
        {
            Failure = ex;
            _logger.LogError(ex, "Unable to present the RDP session through mstscax; aborting.");
            Close();
        }
    }

    private static void SetExtended(IMsRdpExtendedSettings settings, string name, object value)
    {
        settings.set_Property(name, ref value);
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
