using Volo.Abp.Modularity;

namespace Wsm.Tests.Bootstrap;

/// <summary>
/// Root startup module for the test assembly. Composes the core module graph so
/// the bootstrap tests exercise the same assembly wiring the app will use.
/// </summary>
[DependsOn(
    typeof(WindowsSandboxManager.Domain.SandboxDomainModule),
    typeof(Wsm.Engines.Abstractions.SandboxEngineAbstractionsModule),
    typeof(Wsm.Application.SandboxApplicationModule))]
public sealed class TestModule : AbpModule
{
}
