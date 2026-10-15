using Volo.Abp.Modularity;
using Wsm.Engines.Abstractions;

namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// Registers the container desktop channel. Depends only on the engine
/// abstractions: the host drives containers through <see cref="ISandboxEngine"/>
/// and never on a concrete engine, so the service host and the CLI compose it the
/// same way.
/// </summary>
[DependsOn(typeof(SandboxEngineAbstractionsModule))]
public sealed class WsmConnectionContainerDesktopModule : AbpModule
{
}
