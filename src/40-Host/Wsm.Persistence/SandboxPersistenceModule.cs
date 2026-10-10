using Volo.Abp.Modularity;
using WindowsSandboxManager.Domain;

namespace Wsm.Persistence;

/// <summary>
/// ABP module for sandbox instance persistence. Stores each instance as a JSON
/// aggregate record plus a human-editable <c>.wsb</c> recipe sidecar.
/// </summary>
[DependsOn(typeof(SandboxDomainModule))]
public sealed class SandboxPersistenceModule : AbpModule
{
}
