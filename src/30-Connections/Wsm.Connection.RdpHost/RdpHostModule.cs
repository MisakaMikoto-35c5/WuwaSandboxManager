using Volo.Abp.Modularity;

namespace Wsm.Connection.RdpHost;

/// <summary>
/// ABP module for the RDP ActiveX host. Registers <see cref="IRdpSessionHost"/>,
/// the WinForms <c>AxHost</c> presenter that feeds an RDPBASE named-pipe endpoint
/// into <c>mstscax</c> (<c>MsRdpClient10</c>).
/// </summary>
public sealed class RdpHostModule : AbpModule
{
}
