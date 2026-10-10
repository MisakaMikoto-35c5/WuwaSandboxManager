using Volo.Abp.Modularity;
using Wsm.Application.EngineSelection;
using Wsm.Engines.Abstractions;
using Wsm.Persistence;

namespace Wsm.Application;

/// <summary>
/// ABP module for the application layer. Orchestrates use cases over the engine
/// abstractions, the domain model, engine selection and persistence.
/// </summary>
[DependsOn(
    typeof(WindowsSandboxManager.Domain.SandboxDomainModule),
    typeof(SandboxEngineAbstractionsModule),
    typeof(EngineSelectionModule),
    typeof(SandboxPersistenceModule))]
public sealed class SandboxApplicationModule : AbpModule
{
}
