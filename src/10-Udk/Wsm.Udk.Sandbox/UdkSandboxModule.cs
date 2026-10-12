using Volo.Abp.Modularity;

namespace Wsm.Udk.Sandbox;

/// <summary>
/// ABP module for the Windows UDK host. It exposes <see cref="IUdkSandboxHost"/>
/// through conventional DI and depends on nothing else: the UDK is a Windows
/// system component, so no host service or configuration is required.
/// </summary>
public sealed class UdkSandboxModule : AbpModule
{
}
