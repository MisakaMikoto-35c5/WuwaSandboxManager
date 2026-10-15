using Volo.Abp.Modularity;

namespace Wsm.Connection.RemoteApp;

/// <summary>
/// ABP module for the remoteapptool-style RemoteApp kit: the stock Remote
/// Desktop client (<c>mstsc.exe</c>) driven by a generated <c>.rdp</c> document
/// (<c>remoteapplicationmode:i:1</c>), plus the desktop-shortcut writer the GUI
/// uses.
/// <para>
/// This is the <b>alternative</b> RAIL presentation. The default RemoteApp path
/// is the WDAG-style client (<c>Wsm.Connection.RailClient</c>, RDPBASE named
/// pipe + free-floating windows); this kit remains for the
/// <c>remoteapptool</c>-style workflow (an <c>.rdp</c> a user can open by hand)
/// and is the only named-pipe transport that works with the stock client: a
/// sandbox whose RDP endpoint is a named pipe is bridged to loopback TCP by
/// <see cref="IRemoteAppSessionHost"/>.
/// </para>
/// </summary>
public sealed class RemoteAppModule : AbpModule
{
}
