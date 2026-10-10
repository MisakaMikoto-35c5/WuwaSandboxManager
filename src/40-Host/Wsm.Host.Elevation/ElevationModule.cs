using Volo.Abp.Modularity;

namespace Wsm.Host.Elevation;

/// <summary>
/// ABP module for the host elevation capability. Detects whether the current
/// process is elevated and can relaunch it with a UAC prompt. This is a leaf
/// module: it must not depend on the HCS layout capability.
/// </summary>
public sealed class ElevationModule : AbpModule
{
}
