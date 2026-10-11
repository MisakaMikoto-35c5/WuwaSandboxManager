using Volo.Abp.Modularity;
using Wsm.Domain;

namespace Wsm.Hcs.Json.Container;

/// <summary>
/// ABP module for the Windows Container HCS JSON capability. It builds the
/// schema 2.1 container configuration consumed by the
/// <c>Wsm.Engine.WindowsContainer</c> engine, reusing the minimal OS image layer
/// that the Windows Sandbox provisions.
/// </summary>
[DependsOn(typeof(SandboxDomainModule))]
public sealed class ContainerJsonModule : AbpModule
{
}
