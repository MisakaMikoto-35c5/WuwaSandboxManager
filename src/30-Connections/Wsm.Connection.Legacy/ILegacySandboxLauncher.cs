using WindowsSandboxManager.Domain.ValueObjects;

namespace Wsm.Connection.Legacy;

/// <summary>
/// Opens a classic full-desktop Windows Sandbox session by writing the recipe to
/// a temporary <c>.wsb</c> file and launching <c>WindowsSandbox.exe</c>.
/// </summary>
public interface ILegacySandboxLauncher
{
    void Launch(SandboxInstanceId id, SandboxRecipe recipe);
}
