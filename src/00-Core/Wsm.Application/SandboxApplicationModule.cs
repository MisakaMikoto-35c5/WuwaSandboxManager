using Volo.Abp.Modularity;
using Wsm.Engines.Abstractions;

namespace Wsm.Application;

/// <summary>
/// ABP module for the application layer. Orchestrates use cases over the engine
/// abstractions and the domain model.
/// </summary>
[DependsOn(
    typeof(WindowsSandboxManager.Domain.SandboxDomainModule),
    typeof(SandboxEngineAbstractionsModule))]
public sealed class SandboxApplicationModule : AbpModule
{
}
