using System.ComponentModel;
using System.Diagnostics;

namespace Wsm.Engine.WindowsContainer.Internal;

/// <summary>
/// Ensures the Windows host services that the container stack depends on are
/// running: <c>vmcompute</c> (the Host Compute Service that creates the compute
/// system) and <c>hns</c> (the Host Network Service that owns container NICs).
/// <para>
/// This is a best-effort probe driven through <c>sc.exe</c>, so it carries no
/// extra package dependency. It uses the process exit code rather than parsing
/// <c>sc query</c> output, so it is unaffected by the OS display language:
/// <c>ERROR_SERVICE_ALREADY_RUNNING</c> (1056) counts as success.
/// </para>
/// </summary>
internal static class WindowsHostServices
{
    /// <summary><c>ERROR_SERVICE_ALREADY_RUNNING</c>.</summary>
    private const int ServiceAlreadyRunning = 1056;

    /// <summary>
    /// Ensures the named service is running, starting it when needed. Returns
    /// false when it is neither running nor startable.
    /// </summary>
    public static bool TryEnsureRunning(string serviceName)
    {
        var exitCode = RunSc($"start \"{serviceName}\"");
        return exitCode == 0 || exitCode == ServiceAlreadyRunning;
    }

    private static int RunSc(string arguments)
    {
        var info = new ProcessStartInfo("sc.exe", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(info)
                ?? throw new InvalidOperationException("Failed to start sc.exe.");

            // Drain both streams before waiting so a full pipe cannot deadlock.
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);
            return process.HasExited ? process.ExitCode : -1;
        }
        catch (Win32Exception)
        {
            return -1;
        }
    }
}
