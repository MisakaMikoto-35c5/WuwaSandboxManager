using System.Drawing;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;

namespace Wsm.Connection.RdpHost.Internal;

/// <summary>
/// WinForms window that hosts the <c>mstscax</c> ActiveX control and connects it
/// to an RDPBASE named-pipe endpoint. It is created on a dedicated STA thread by
/// <see cref="WinFormsRdpSessionHost"/>; when the window closes the hosting task
/// completes.
/// </summary>
internal sealed class RdpHostForm : Form
{
    private readonly RdpSessionRequest _request;
    private readonly ILogger _logger;
    private MsRdpClientAxHost? _client;

    public RdpHostForm(RdpSessionRequest request, ILogger logger)
    {
        _request = request;
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
    /// completion task with it so the caller can surface the failure (and fall
    /// back) instead of silently showing nothing.
    /// </summary>
    public Exception? Failure { get; private set; }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        try
        {
            _client = new MsRdpClientAxHost { Dock = DockStyle.Fill };
            Controls.Add(_client);
            Connect();
        }
        catch (Exception ex)
        {
            Failure = ex;
            _logger.LogError(ex, "Unable to present the RDP session through mstscax; aborting the direct path.");
            Close();
        }
    }

    private void Connect()
    {
        var ocx = _client!.GetOcx()
            ?? throw new InvalidOperationException("The mstscax ActiveX control instance was not created.");

        // N5: the endpoint RCW is produced on the RDPBASE callback thread and
        // used here on the hosting STA thread. Probe whether it is agile so a
        // cross-apartment mismatch is surfaced rather than silently failing.
        ComApartment.WarnIfNotAgile(_request.Endpoint, _logger);

        ComPropertyAccessor.TrySet(ocx, "Server", string.IsNullOrWhiteSpace(_request.WindowTitle) ? "Windows Sandbox" : _request.WindowTitle);
        if (!string.IsNullOrEmpty(_request.UserName))
        {
            ComPropertyAccessor.TrySet(ocx, "UserName", _request.UserName);
        }

        // ConnectWithEndpoint lives on the version-8+ advanced settings interface.
        var advanced = ComPropertyAccessor.TryGetFirst(
            ocx,
            "AdvancedSettings10",
            "AdvancedSettings9",
            "AdvancedSettings8",
            "AdvancedSettings5",
            "AdvancedSettings");

        if (advanced is null)
        {
            // N4: without this interface the endpoint cannot be supplied, so
            // connecting would use no endpoint at all. Abort loudly.
            throw new InvalidOperationException(
                "mstscax did not expose an AdvancedSettings interface, so ConnectWithEndpoint could not be set. " +
                "The RDPBASE stream was acquired but the control cannot be connected; aborting instead of connecting without an endpoint.");
        }

        if (!ComPropertyAccessor.TrySet(advanced, "ConnectWithEndpoint", _request.Endpoint))
        {
            // N4: the endpoint is mandatory for this transport. Surface the
            // specific failure and abort rather than calling Connect() bare.
            throw new InvalidOperationException(
                "mstscax AdvancedSettings.ConnectWithEndpoint rejected the RDPBASE endpoint. " +
                "The stream was acquired but cannot be presented; aborting instead of connecting without an endpoint.");
        }

        ComPropertyAccessor.TrySet(advanced, "EnableCredSspSupport", true);
        ComPropertyAccessor.TrySet(advanced, "NegotiateSecurityLayer", true);
        ComPropertyAccessor.TrySet(advanced, "AuthenticationLevel", 0);
        ComPropertyAccessor.TrySet(advanced, "RedirectClipboard", _request.Redirections.Clipboard);
        ComPropertyAccessor.TrySet(advanced, "RedirectPrinters", _request.Redirections.Printer);
        ComPropertyAccessor.TrySet(advanced, "RedirectSmartCards", _request.Redirections.SmartCard);

        var remoteProgram = ComPropertyAccessor.TryGetFirst(ocx, "RemoteProgram3", "RemoteProgram2", "RemoteProgram");
        if (remoteProgram is not null)
        {
            ComPropertyAccessor.TrySet(remoteProgram, "RemoteProgramMode", _request.RemoteApp);
        }
        else if (_request.RemoteApp)
        {
            _logger.LogWarning("mstscax did not expose a RemoteProgram interface; RAIL mode could not be enabled.");
        }

        if (!ComPropertyAccessor.TryInvoke(ocx, "Connect"))
        {
            throw new InvalidOperationException(
                "mstscax Connect() failed; the RDP session could not be started.");
        }
    }
}
