namespace Wsm.Host.Environment.Internal;

/// <summary>
/// Resolves the modern Windows Sandbox client executable across the packaged
/// AppExecutionAlias reparse points (<c>%LocalAppData%\Microsoft\WindowsApps</c>),
/// the package install directory and <c>System32</c>. The modern client is either
/// the per-instance remote-session client (<c>WindowsSandboxRemoteSession.exe</c>)
/// or the orchestrating <c>WindowsSandboxClient.exe</c>; the first existing
/// candidate wins.
/// </summary>
internal static class SandboxClientResolver
{
    /// <summary>Candidate modern client names, most specific first.</summary>
    public static readonly string[] ModernClientNames =
    {
        "WindowsSandboxRemoteSession.exe",
        "WindowsSandboxClient.exe",
    };

    /// <summary>
    /// Returns the first existing modern client path, or <c>null</c> when none of
    /// the candidate names exist in any of the candidate locations.
    /// </summary>
    public static string? Resolve(
        string? packageInstallPath,
        string windowsAppsDirectory,
        string systemDirectory,
        Func<string, bool> fileExists)
    {
        foreach (var name in ModernClientNames)
        {
            var candidates = new[]
            {
                Path.Combine(windowsAppsDirectory, name),
                string.IsNullOrWhiteSpace(packageInstallPath) ? null : Path.Combine(packageInstallPath!, name),
                Path.Combine(systemDirectory, name),
            };

            foreach (var candidate in candidates)
            {
                if (candidate is not null && fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
