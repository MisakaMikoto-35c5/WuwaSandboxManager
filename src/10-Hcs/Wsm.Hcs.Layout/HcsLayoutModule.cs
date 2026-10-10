using Volo.Abp.Modularity;

namespace Wsm.Hcs.Layout;

/// <summary>
/// ABP module for the HCS host layout capability. Resolves the on-disk container
/// layout, base OS image layer and process elevation state through
/// <see cref="IHcsHostLayout"/>.
/// </summary>
public sealed class HcsLayoutModule : AbpModule
{
}
