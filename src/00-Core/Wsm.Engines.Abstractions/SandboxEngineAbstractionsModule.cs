using Volo.Abp.Modularity;

namespace Wsm.Engines.Abstractions;

/// <summary>
/// ABP module for the engine abstraction layer. Declares the single, unified
/// <c>ISandboxEngine</c> contract every concrete engine implements.
/// </summary>
[DependsOn(typeof(Wsm.Domain.SandboxDomainModule))]
public sealed class SandboxEngineAbstractionsModule : AbpModule
{
}
