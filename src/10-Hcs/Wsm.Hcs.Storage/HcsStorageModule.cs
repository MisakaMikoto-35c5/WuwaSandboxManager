using Volo.Abp.Modularity;
using Wsm.Domain;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Layout;

namespace Wsm.Hcs.Storage;

/// <summary>
/// ABP module for the HCS user-data storage capability. Provisions and seeds the
/// per-sandbox storage directory through <see cref="ISandboxStorageProvisioner"/>.
/// Depends on <see cref="HcsInteropModule"/> so the provisioner can create the
/// guest-state file with <c>HcsCreateEmptyGuestStateFile</c> when a storage is
/// being created from scratch.
/// </summary>
[DependsOn(
    typeof(HcsInteropModule),
    typeof(HcsLayoutModule),
    typeof(SandboxDomainModule))]
public sealed class HcsStorageModule : AbpModule
{
}
