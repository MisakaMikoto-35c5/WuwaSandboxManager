using Volo.Abp.Modularity;
using Wsm.Engines.Abstractions;

namespace Wsm.Application.EngineSelection;

/// <summary>
/// ABP module for engine selection. Owns <see cref="ISandboxEngineSelector"/>,
/// which picks the engine to serve a start request (HCS first by default).
/// </summary>
[DependsOn(typeof(SandboxEngineAbstractionsModule))]
public sealed class EngineSelectionModule : AbpModule
{
}
