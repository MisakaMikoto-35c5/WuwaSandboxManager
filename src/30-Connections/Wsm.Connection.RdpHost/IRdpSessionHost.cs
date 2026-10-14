namespace Wsm.Connection.RdpHost;

/// <summary>
/// Presents an RDP session over a pre-acquired network-stream endpoint. The
/// default implementation hosts the <c>mstscax</c> <c>MsRdpClient10</c> ActiveX
/// control on a dedicated STA window, which is how the official Windows Sandbox
/// client and WDAG's <c>HvsiRdpClient</c> display a sandbox without guest
/// networking.
/// </summary>
public interface IRdpSessionHost
{
    /// <summary>
    /// Shows the session and completes when the user closes the window (or the
    /// session terminates).
    /// </summary>
    Task ShowAsync(RdpSessionRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes every open session window. Closing runs each form's normal
    /// teardown, which disconnects the <c>mstscax</c> client gracefully. Called
    /// before a sandbox is stopped so an auto-reconnecting client cannot hold —
    /// or immediately re-establish — the guest session while the compute system
    /// is being shut down.
    /// </summary>
    void CloseAll();
}
