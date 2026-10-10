using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WindowsSandboxManager.Domain.Abstractions;
using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;

namespace WindowsSandboxManager.Infrastructure.Connectors;

/// <summary>
/// Opens the user-visible RDP session for a running sandbox.
/// <para>
/// The primary path launches the packaged <c>WindowsSandboxRemoteSession.exe</c>
/// client, which implements both the full-desktop ("Remote Desktop style") and
/// the RAIL/RemoteApp ("individual windows") presentations. The presentation is
/// chosen by the <c>RailMode</c> flag baked into the sandbox when it was started.
/// </para>
/// <para>
/// A legacy fallback writes a <c>.wsb</c> file and launches
/// <c>WindowsSandbox.exe</c> for a classic full-desktop session.
/// </para>
/// </summary>
public sealed class WindowsSandboxSessionConnector : ISandboxSessionConnector
{
    private readonly ISandboxEnvironment _environment;
    private readonly ISandboxRecipeSerializer _serializer;
    private readonly ILogger<WindowsSandboxSessionConnector> _logger;

    public WindowsSandboxSessionConnector(
        ISandboxEnvironment environment,
        ISandboxRecipeSerializer serializer,
        ILogger<WindowsSandboxSessionConnector> logger)
    {
        _environment = environment;
        _serializer = serializer;
        _logger = logger;
    }

    public Task ConnectAsync(
        SandboxInstanceId id,
        SandboxRecipe recipe,
        SandboxRuntimeInfo runtime,
        SandboxConnectionMode mode,
        CancellationToken cancellationToken = default)
    {
        if (mode == SandboxConnectionMode.RemoteApp && !runtime.RailMode)
        {
            _logger.LogWarning(
                "Requested RemoteApp mode but sandbox {SandboxId} was started without RailMode. " +
                "RAIL is fixed at start time; restart the sandbox in RemoteApp mode to get seamless windows.", id);
        }

        var remoteSession = _environment.RemoteSessionExecutablePath;
        if (remoteSession is not null)
        {
            _logger.LogInformation("Connecting to sandbox {SandboxId} via WindowsSandboxRemoteSession.exe ({Mode})", id, mode);
            Process.Start(new ProcessStartInfo
            {
                FileName = remoteSession,
                Arguments = $"--id {id}",
                UseShellExecute = true,
            });
            return Task.CompletedTask;
        }

        var legacy = _environment.LegacyExecutablePath;
        if (legacy is not null)
        {
            var wsbPath = WriteTemporaryRecipe(id, recipe);
            _logger.LogInformation("Connecting to sandbox {SandboxId} via legacy WindowsSandbox.exe", id);
            Process.Start(new ProcessStartInfo
            {
                FileName = legacy,
                Arguments = $"\"{wsbPath}\"",
                UseShellExecute = true,
            });
            return Task.CompletedTask;
        }

        throw new SandboxNotSupportedException(
            "No Windows Sandbox client executable (WindowsSandboxRemoteSession.exe / WindowsSandbox.exe) was found.");
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
