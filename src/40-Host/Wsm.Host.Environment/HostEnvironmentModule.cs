using Volo.Abp.Modularity;
using Wsm.Host.Elevation;
using Wsm.Host.Policy;

namespace Wsm.Host.Environment;

/// <summary>
/// ABP module for the host environment capability. Detects the installed Windows
/// Sandbox feature, the control-plane pipe name and the relevant executables.
/// </summary>
[DependsOn(typeof(MultiInstancePolicyModule), typeof(ElevationModule))]
public sealed class HostEnvironmentModule : AbpModule
{
}
