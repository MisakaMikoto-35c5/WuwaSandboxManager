using System.Drawing;
using System.Windows.Forms;
using AxMSTSCLib;
using MSTSCLib;
using Wsm.Connection.RdpHost;

namespace Wsm.Connection.RdpHost.Ax;

/// <summary>
/// The shared <c>mstscax</c> ActiveX host. It is a plain WinForms control so it
/// can be embedded by both connector presentations: the WPF connector hosts it
/// through a <c>WindowsFormsHost</c>, and the WinUI 3 connector hosts its HWND as
/// a child window. It owns the strongly typed <see cref="AxMsRdpClient10NotSafeForScripting"/>
/// and exposes a UI-agnostic API (connect, disconnect, send Ctrl+Alt+Del) plus
/// session events.
/// <para>
/// The control must be constructed and connected on an STA thread that pumps
/// messages (the WPF dispatcher or the WinUI dispatcher): the RDPBASE completion
/// callback and the ActiveX itself require it.
/// </para>
/// </summary>
public sealed class RdpClientControl : UserControl
{
    /// <summary>
    /// Process-wide gate around <c>IMsRdpClient10.Connect</c>. mstscax performs
    /// one-time, process-global initialization inside <c>Connect</c> (window
    /// class registration and shared state) without any synchronization: two
    /// controls connecting at the same moment in this process fail with
    /// <c>ERROR_CLASS_ALREADY_EXISTS</c> ("class already exists") or a bogus
    /// <c>OutOfMemoryException</c>. Connects are therefore serialized.
    /// </summary>
    private static readonly SemaphoreSlim ConnectGate = new(1, 1);

    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;   // ALT
    private const int VkDelete = 0x2E;

    private readonly AxMsRdpClient10NotSafeForScripting _client;
    private volatile bool _connected;

    public RdpClientControl()
    {
        BackColor = Color.Black;
        Dock = DockStyle.Fill;

        _client = new AxMsRdpClient10NotSafeForScripting { Dock = DockStyle.Fill };
        Controls.Add(_client);

        _client.OnConnected += (_, _) =>
        {
            _connected = true;
            Connected?.Invoke(this, EventArgs.Empty);
        };
        _client.OnDisconnected += (_, args) =>
        {
            _connected = false;
            Disconnected?.Invoke(this, new RdpDisconnectEventArgs(args.discReason));
        };
        _client.OnFatalError += (_, args) =>
        {
            _connected = false;
            FatalError?.Invoke(this, args.errorCode);
        };
        _client.OnLogonError += (_, args) => LogonError?.Invoke(this, args.lError);
    }

    /// <summary>Raised when the session connects.</summary>
    public event EventHandler? Connected;

    /// <summary>Raised when the session is disconnected (locally or remotely).</summary>
    public event EventHandler<RdpDisconnectEventArgs>? Disconnected;

    /// <summary>Raised on a fatal session error (carries the raw error code).</summary>
    public event EventHandler<int>? FatalError;

    /// <summary>Raised on a logon error (carries the raw logon error code).</summary>
    public event EventHandler<int>? LogonError;

    /// <summary>Set when <see cref="ConnectAsync"/> could not present the session.</summary>
    public Exception? ConnectFailure { get; private set; }

    /// <summary>Whether the session is currently connected.</summary>
    public bool IsConnected => _connected && _client.Connected != 0;

    /// <summary>
    /// Configures and connects the ActiveX client. <paramref name="endpointProvider"/>
    /// acquires the RDPBASE named-pipe endpoint (named-pipe transport); it is null
    /// for the TCP transport.
    /// </summary>
    public async Task ConnectAsync(
        RdpSessionRequest request,
        Func<CancellationToken, Task<object>>? endpointProvider,
        CancellationToken cancellationToken = default)
    {
        try
        {
            object? endpoint = null;
            if (string.IsNullOrWhiteSpace(request.ServerAddress))
            {
                if (endpointProvider is null)
                {
                    throw new InvalidOperationException("A named-pipe session requires an endpoint provider.");
                }

                endpoint = await endpointProvider(cancellationToken).ConfigureAwait(true);
            }

            ApplySettings(request);

            if (request.ServerAddress is { Length: > 0 })
            {
                _client.Server = request.ServerAddress;
                _client.AdvancedSettings9.RDPPort = request.ServerPort;
            }
            else
            {
                var endpointObject = endpoint!;
                _client.AdvancedSettings9.set_ConnectWithEndpoint(ref endpointObject);
            }

            await ConnectGate.WaitAsync(cancellationToken).ConfigureAwait(true);
            try
            {
                _client.Connect();
            }
            finally
            {
                ConnectGate.Release();
            }
        }
        catch (Exception ex)
        {
            ConnectFailure = ex;
            throw;
        }
    }

    /// <summary>
    /// Sends Ctrl+Alt+Del to the session through the ActiveX
    /// <c>IMsRdpClientNonScriptable.SendKeys</c> SAS support.
    /// </summary>
    public void SendCtrlAltDel()
    {
        if (!IsConnected)
        {
            return;
        }

        try
        {
            var nonScriptable = (IMsRdpClientNonScriptable)_client.GetOcx()!;
            var keys = new[] { VkControl, VkMenu, VkDelete };

            var down = new[] { true, true, true };
            nonScriptable.SendKeys(keys.Length, ref down[0], ref keys[0]);

            var up = new[] { false, false, false };
            nonScriptable.SendKeys(keys.Length, ref up[0], ref keys[0]);
        }
        catch (Exception ex)
        {
            ConnectFailure ??= ex;
        }
    }

    /// <summary>
    /// Re-applies the remote desktop size after the hosting surface was resized,
    /// so the guest desktop follows the window. No-op until connected.
    /// </summary>
    public void UpdateRemoteDesktopSize()
    {
        if (_client is null || !IsConnected)
        {
            return;
        }

        try
        {
            var width = Math.Max(Width, 200);
            var height = Math.Max(Height, 150);
            if (_client.DesktopWidth != width)
            {
                _client.DesktopWidth = width;
            }

            if (_client.DesktopHeight != height)
            {
                _client.DesktopHeight = height;
            }
        }
        catch
        {
            // Container/framebuffer sessions may refuse a live resize; SmartSizing
            // still scales the remote image to the control.
        }
    }

    /// <summary>Disconnects the client gracefully (best effort).</summary>
    public void Disconnect()
    {
        try
        {
            if (_client.Connected != 0)
            {
                _client.Disconnect();
            }
        }
        catch
        {
            // Best effort: the control may already be gone.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Disconnect();
        }

        base.Dispose(disposing);
    }

    private void ApplySettings(RdpSessionRequest request)
    {
        var advanced = _client.AdvancedSettings9;

        // Mirror the platform's own remote-session client: NLA is not used, the
        // security layer is negotiated, server authentication is not required, and
        // the credential is supplied in clear text.
        advanced.EnableCredSspSupport = false;
        advanced.NegotiateSecurityLayer = true;
        advanced.AuthenticationLevel = 0;

        advanced.DisableCtrlAltDel = 1;
        advanced.DisplayConnectionBar = true;
        advanced.RedirectClipboard = request.Redirections.Clipboard;
        advanced.RedirectPrinters = request.Redirections.Printer;
        advanced.RedirectSmartCards = request.Redirections.SmartCard;
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

        _client.UserName = request.UserName;
        advanced.ClearTextPassword = request.Password;

        var extended = (IMsRdpExtendedSettings)_client.GetOcx()!;
        // The workspace id scopes the client's per-process session tracking; it
        // must differ per sandbox, or connections made from this process are
        // treated as one workspace and a new connect bumps the previous session.
        SetExtended(
            extended,
            "Workspace Id",
            request.VmId != Guid.Empty ? $"in WindowsSandbox:{request.VmId}" : "in WindowsSandbox");

        if (string.IsNullOrWhiteSpace(request.ServerAddress))
        {
            // Container/pipe-only knobs. The HiDef container channel drives full
            // screen and the framebuffer and routes by container GUID; over TCP
            // there is no container channel and setting these crashes the control.
            advanced.ContainerHandledFullScreen = 1;
            SetExtended(extended, "ClientDeviceName", request.VmId.ToString());
            SetExtended(extended, "EnableHardwareMode", true);
            SetExtended(extended, "EnableFrameBufferRedirection", true);
            if (request.VmId != Guid.Empty)
            {
                SetExtended(extended, "HiDefRemoteAppContainerGUID", $"{{{request.VmId}}}");
            }
        }

        SetExtended(extended, "AllowSasSequenceTransmission", false);
        SetExtended(extended, "DisableAutoReconnectComponent", true);

        if (request.RemoteApp)
        {
            _client.RemoteProgram3.RemoteProgramMode = true;

            // The WDAG guest is RemoteApp-only: it has no explorer desktop and does
            // not publish an application list, so the client must name the program
            // to start. Without it the guest tears the empty session down.
            if (!string.IsNullOrWhiteSpace(request.RemoteAppProgram))
            {
                _client.RemoteProgram3.RemoteApplicationProgram = request.RemoteAppProgram;
            }
        }
    }

    private static void SetExtended(IMsRdpExtendedSettings settings, string name, object value)
        => settings.set_Property(name, ref value);
}

/// <summary>Disconnection details raised by <see cref="RdpClientControl.Disconnected"/>.</summary>
public sealed class RdpDisconnectEventArgs : EventArgs
{
    public RdpDisconnectEventArgs(int reason)
    {
        Reason = reason;
    }

    /// <summary>The raw <c>OnDisconnected</c> reason code.</summary>
    public int Reason { get; }

    /// <summary>A short, human-readable description of <see cref="Reason"/>.</summary>
    public string ReasonText => Describe(Reason);

    private static string Describe(int discReason) => discReason switch
    {
        // Values are the documented IMsTscAxEvents::OnDisconnected disconnectReason
        // codes (Microsoft Learn: "IMsTscAxEvents OnDisconnected method").
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
}

