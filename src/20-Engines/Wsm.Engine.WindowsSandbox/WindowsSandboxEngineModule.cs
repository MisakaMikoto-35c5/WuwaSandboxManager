using Volo.Abp.Modularity;
using Wsm.Engines.Abstractions;
using Wsm.Host.Environment;
using Wsm.Recipe.Serialization;
using Wsm.SandboxServer.Client;

namespace Wsm.Engine.WindowsSandbox;

/// <summary>
/// ABP module for the non-elevated Windows Sandbox engine. Composes the
/// control-plane gRPC client, the .wsb recipe serializer and the host
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
    typeof(SandboxServerClientModule),
    typeof(WsbRecipeSerializationModule),
    typeof(HostEnvironmentModule))]
public sealed class WindowsSandboxEngineModule : AbpModule
{
}
