using Volo.Abp.Modularity;
using Wsm.Domain;
using Wsm.Recipe.Serialization;

namespace Wsm.Persistence;

/// <summary>
/// ABP module for sandbox persistence. Each sandbox is stored as a single,
/// portable <c>.wsm</c> file that embeds its complete configuration and a
/// relative user-data path.
/// </summary>
[DependsOn(typeof(SandboxDomainModule), typeof(WsbRecipeSerializationModule))]
public sealed class SandboxPersistenceModule : AbpModule
{
}
