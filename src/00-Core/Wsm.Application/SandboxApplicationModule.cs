using Volo.Abp.Modularity;
using Wsm.Application.EngineSelection;
using Wsm.Connection.Session;
using Wsm.Engines.Abstractions;
using Wsm.Persistence;

namespace Wsm.Application;

/// <summary>
/// ABP module for the application layer. Orchestrates use cases over the engine
/// abstractions, the domain model, engine selection and persistence.
/// </summary>
[DependsOn(
    typeof(Wsm.Domain.SandboxDomainModule),
    typeof(SandboxEngineAbstractionsModule),
    typeof(EngineSelectionModule),
    typeof(SandboxPersistenceModule),
    typeof(SessionLaunchModule))]
public sealed class SandboxApplicationModule : AbpModule
{
}
