using Volo.Abp.Modularity;
using Wsm.Hcs.Layout;

namespace Wsm.Hcs.Storage;

/// <summary>
/// ABP module for the HCS user-data storage capability. Provisions and seeds the
/// per-sandbox storage directory through <see cref="ISandboxStorageProvisioner"/>.
/// </summary>
[DependsOn(typeof(HcsLayoutModule))]
public sealed class HcsStorageModule : AbpModule
{
}
