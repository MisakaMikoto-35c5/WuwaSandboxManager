using Volo.Abp.Modularity;
using Wsm.Engines.Abstractions;
using Wsm.Host.Environment;
using Wsm.Udk.Sandbox;

namespace Wsm.Engine.WindowsSandbox;

/// <summary>
/// ABP module for the Windows Sandbox engine. It composes the Windows UDK host
/// (which creates the VM exactly like Windows Sandbox itself) and the host
/// environment probe, and exposes <see cref="ISandboxEngine"/> through
/// conventional DI.
/// <para>
/// It deliberately does <b>not</b> depend on <c>SessionLaunchModule</c>: no
/// <see cref="ISandboxEngine"/> member maps to launching a user session, which
/// lives in the application layer.
/// </para>
/// </summary>
[DependsOn(
    typeof(SandboxEngineAbstractionsModule),
    typeof(UdkSandboxModule),
    typeof(HostEnvironmentModule))]
public sealed class WindowsSandboxEngineModule : AbpModule
{
}
