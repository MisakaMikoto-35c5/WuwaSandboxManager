using Volo.Abp.Modularity;
using Wsm.Connection.RdpBase;

namespace Wsm.Connection.RdpHost;

/// <summary>
/// ABP module for the RDP ActiveX host. Registers <see cref="IRdpSessionHost"/>,
/// the WinForms presenter that acquires the RDPBASE named-pipe endpoint on an STA
/// thread and feeds it into the strongly typed <c>mstscax</c> control.
/// </summary>
[DependsOn(typeof(RdpBaseModule))]
public sealed class RdpHostModule : AbpModule
{
}
