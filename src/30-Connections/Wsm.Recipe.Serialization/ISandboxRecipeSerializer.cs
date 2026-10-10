using Wsm.Domain.ValueObjects;

namespace Wsm.Recipe.Serialization;

/// <summary>
/// Converts a <see cref="SandboxRecipe"/> to/from the on-the-wire representation
/// (the .wsb XML understood by Windows Sandbox).
/// </summary>
public interface ISandboxRecipeSerializer
{
    string Serialize(SandboxRecipe recipe);

    SandboxRecipe Deserialize(string payload);
}
