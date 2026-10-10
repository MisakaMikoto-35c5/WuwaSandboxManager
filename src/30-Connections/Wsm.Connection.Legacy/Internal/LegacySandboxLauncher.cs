using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Host.Environment;
using Wsm.Recipe.Serialization;

namespace Wsm.Connection.Legacy.Internal;

/// <summary>
/// Default <see cref="ILegacySandboxLauncher"/>. Serializes the recipe to a
/// per-instance temporary <c>.wsb</c> file and launches
/// <c>WindowsSandbox.exe</c> against it.
/// <para>
/// <b>This starts a new sandbox, it does not connect to an existing one.</b>
/// <c>WindowsSandbox.exe</c> has no re-attach semantics; callers that already
/// have a running instance must not treat this as a reconnect.
/// </para>
/// </summary>
[ExposeServices(typeof(ILegacySandboxLauncher))]
internal sealed class LegacySandboxLauncher : ILegacySandboxLauncher, ISingletonDependency
{
    private readonly ISandboxEnvironment _environment;
    private readonly ISandboxRecipeSerializer _serializer;
    private readonly ILogger<LegacySandboxLauncher> _logger;

    public LegacySandboxLauncher(
        ISandboxEnvironment environment,
        ISandboxRecipeSerializer serializer,
        ILogger<LegacySandboxLauncher> logger)
    {
        _environment = environment;
        _serializer = serializer;
        _logger = logger;
    }

    public void Launch(SandboxInstanceId id, SandboxRecipe recipe)
    {
        var legacy = _environment.LegacyExecutablePath;
        if (legacy is null)
        {
            throw new SandboxNotSupportedException(
                "The legacy Windows Sandbox client executable (WindowsSandbox.exe) was not found.");
        }

        var wsbPath = WriteTemporaryRecipe(id, recipe);
        _logger.LogInformation(
            "Starting a NEW sandbox {SandboxId} via the legacy WindowsSandbox.exe path (this is not a reconnect to an existing instance).",
            id);
        Process.Start(new ProcessStartInfo
        {
            FileName = legacy,
            Arguments = $"\"{wsbPath}\"",
            UseShellExecute = true,
        });
    }

    private string WriteTemporaryRecipe(SandboxInstanceId id, SandboxRecipe recipe)
    {
        var directory = TemporarySandboxFiles.DirectoryPath;

        // Ensure the directory exists and drop transient files left behind by
        // earlier launches before writing the new recipe.
        TemporarySandboxFiles.Prepare(directory);

        var path = TemporarySandboxFiles.PathFor(directory, id, TemporarySandboxFiles.RecipeExtension);
        File.WriteAllText(path, _serializer.Serialize(recipe));
        return path;
    }
}
