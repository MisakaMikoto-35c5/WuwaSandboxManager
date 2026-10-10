using Volo.Abp.Modularity;

namespace Wsm.Hcs.Json.Sandbox;

/// <summary>
/// ABP module for the Sandbox (VM) HCS JSON capability. Builds the compute
/// system configuration document consumed by <c>HcsCreateComputeSystem</c>
/// through <see cref="ISandboxVmJsonBuilder"/>.
/// </summary>
public sealed class SandboxVmJsonModule : AbpModule
{
}
