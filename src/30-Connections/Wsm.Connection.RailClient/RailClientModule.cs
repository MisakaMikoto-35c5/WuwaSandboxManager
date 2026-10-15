using Volo.Abp.Modularity;
using Wsm.Connection.RdpBase;

namespace Wsm.Connection.RailClient;

/// <summary>
/// ABP module for the <b>WDAG-style RemoteApp client</b>. This is a sibling of
/// the windowed connector (<c>Wsm.Connection.RdpHost</c>) that mirrors how WDAG's
/// <c>hvsirdpclient.exe</c> and the Windows Sandbox remote-session client present
/// RemoteApp sessions:
/// <list type="bullet">
/// <item>a <b>dedicated client process</b> per session (the GUI runs every
/// session in its own <c>wsm connect</c> child, like the platform's
/// one-client-per-sandbox model);</item>
/// <item>the <c>mstscax</c> ActiveX control is hosted on an STA thread over the
/// <b>RDPBASE named pipe</b> (<c>ConnectWithEndpoint</c>) with
/// <c>RemoteProgram3.RemoteProgramMode</c> enabled;</item>
/// <item>the host window is only an invisible <b>status/broker window</b>: while
/// the session starts it shows a small status dialog, and once the remote
/// program appears the dialog is hidden with an <b>empty window region</b>
/// (<c>SetWindowRgn</c>, the same technique the platform client uses) — the
/// remote application windows themselves are free-floating top-level windows
/// that can be dragged, stacked and minimized like local applications.</item>
/// </list>
/// </summary>
[DependsOn(typeof(RdpBaseModule))]
public sealed class RailClientModule : AbpModule
{
}
