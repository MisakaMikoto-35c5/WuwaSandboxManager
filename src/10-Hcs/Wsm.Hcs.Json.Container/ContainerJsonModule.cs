using Volo.Abp.Modularity;
using Wsm.Domain;

namespace Wsm.Hcs.Json.Container;

/// <summary>
/// ABP module for the Windows Container HCS JSON capability.
/// <para>
/// <b>Reserved / not implemented yet.</b> This module is a compilable shell that
/// is registered so the module graph is complete; the JSON builder throws
/// <see cref="Wsm.Domain.Exceptions.SandboxNotSupportedException"/>
/// until a later phase implements it.
/// </para>
/// </summary>
[DependsOn(typeof(SandboxDomainModule))]
public sealed class ContainerJsonModule : AbpModule
{
}
