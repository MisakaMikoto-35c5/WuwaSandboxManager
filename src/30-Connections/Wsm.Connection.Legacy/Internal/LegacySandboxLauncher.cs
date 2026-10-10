using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Host.Environment;
using Wsm.Recipe.Serialization;

namespace Wsm.Connection.Legacy.Internal;

/// <summary>
/// Default <see cref="ILegacySandboxLauncher"/>. Serializes the recipe to a
/// per-instance temporary <c>.wsb</c> file and launches
/// <c>WindowsSandbox.exe</c> against it.
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
        _logger.LogInformation("Connecting to sandbox {SandboxId} via legacy WindowsSandbox.exe", id);
        Process.Start(new ProcessStartInfo
        {
            FileName = legacy,
            Arguments = $"\"{wsbPath}\"",
            UseShellExecute = true,
        });
    }

    private string WriteTemporaryRecipe(SandboxInstanceId id, SandboxRecipe recipe)
    {
        var directory = Path.Combine(Path.GetTempPath(), "WindowsSandboxManager");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{id.Value:D}.wsb");
        File.WriteAllText(path, _serializer.Serialize(recipe));
        return path;
    }
}
