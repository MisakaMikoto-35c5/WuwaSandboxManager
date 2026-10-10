using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.Legacy;

/// <summary>
/// Starts a classic full-desktop Windows Sandbox by writing the recipe to a
/// temporary <c>.wsb</c> file and launching <c>WindowsSandbox.exe</c>.
/// <para>
/// <b>This creates a new sandbox; it does not connect to an existing
/// instance.</b> The supplied <see cref="SandboxInstanceId"/> only names the
/// temporary recipe file.
/// </para>
/// </summary>
public interface ILegacySandboxLauncher
{
    void Launch(SandboxInstanceId id, SandboxRecipe recipe);
}
