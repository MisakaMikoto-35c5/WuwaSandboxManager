using Volo.Abp.Modularity;
using Wsm.Host.Environment;

namespace Wsm.SandboxServer.Client;

/// <summary>
/// ABP module for the Windows Sandbox control-plane client. Drives
/// <c>WindowsSandboxServer.exe</c> through its named-pipe gRPC service via
/// <see cref="ISandboxServerClient"/>.
/// </summary>
[DependsOn(typeof(HostEnvironmentModule))]
public sealed class SandboxServerClientModule : AbpModule
{
}
