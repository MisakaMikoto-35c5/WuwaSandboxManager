using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.RdpHost;

/// <summary>
/// A concrete RDP presentation implemented by a connector module. Every module
/// (WPF, WinUI 3) provides one tagged with its <see cref="RdpConnectorKind"/>;
/// <see cref="SelectingRdpSessionHost"/> picks the active one.
/// <para>
/// This is the in-process contract. A host may derive the presentation from a
/// dedicated child process (WinUI), but <see cref="ShowAsync"/> completes only
/// when the user-visible session window closes, exactly like the WPF host.
/// </para>
/// </summary>
public interface IRdpConnectorHost
{
    /// <summary>Which connector this is.</summary>
    RdpConnectorKind Kind { get; }

    /// <summary>
    /// Shows the session and completes when the window closes (or the session
    /// terminates). <paramref name="control"/> drives the toolbar's lifecycle
    /// buttons and may be null when the composition has no sandbox manager.
    /// </summary>
    Task ShowAsync(RdpSessionRequest request, IRdpSandboxControl? control, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes every open session window. Closing runs each window's normal
    /// teardown, which disconnects the <c>mstscax</c> client gracefully. Called
    /// before a sandbox is stopped so an auto-reconnecting client cannot hold —
    /// or immediately re-establish — the guest session while the compute system
    /// is being shut down.
    /// </summary>
    void CloseAll();
}
