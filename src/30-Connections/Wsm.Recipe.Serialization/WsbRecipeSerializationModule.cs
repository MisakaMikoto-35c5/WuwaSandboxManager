using Volo.Abp.Modularity;
using WindowsSandboxManager.Domain;

namespace Wsm.Recipe.Serialization;

/// <summary>
/// ABP module for the .wsb recipe (de)serialization capability. Converts a
/// <c>SandboxRecipe</c> to/from the XML payload understood by Windows Sandbox
/// through <see cref="ISandboxRecipeSerializer"/>.
/// </summary>
[DependsOn(typeof(SandboxDomainModule))]
public sealed class WsbRecipeSerializationModule : AbpModule
{
}
