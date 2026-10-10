using Volo.Abp.Modularity;
using Wsm.Host.Environment;
using Wsm.Recipe.Serialization;

namespace Wsm.Connection.Legacy;

/// <summary>
/// ABP module for the legacy connection path. Writes a temporary <c>.wsb</c>
/// recipe and launches the classic <c>WindowsSandbox.exe</c> full-desktop client
/// through <see cref="ILegacySandboxLauncher"/>.
/// </summary>
[DependsOn(
    typeof(WsbRecipeSerializationModule),
    typeof(HostEnvironmentModule))]
public sealed class LegacyLauncherModule : AbpModule
{
}
