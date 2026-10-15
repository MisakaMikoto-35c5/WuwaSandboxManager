namespace Wsm.Service.Client;

/// <summary>
/// Locates <c>Wsm.Service.Host.exe</c>: next to the running client executable
/// first, then under a <c>40-Host/Wsm.Service.Host</c> project tree found while
/// walking up towards the repository root (development-tree layout).
/// </summary>
public static class ServiceHostExeLocator
{
    /// <summary>Returns the full path to the service host executable, or <c>null</c>.</summary>
    public static string? Find()
    {
        const string exeName = "Wsm.Service.Host.exe";

        var direct = Path.Combine(AppContext.BaseDirectory, exeName);
        if (File.Exists(direct))
        {
            return direct;
        }

        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            foreach (var projectDirectory in new[]
                     {
                         Path.Combine(current.FullName, "src", "40-Host", "Wsm.Service.Host"),
                         Path.Combine(current.FullName, "40-Host", "Wsm.Service.Host"),
                     })
            {
                var binDirectory = Path.Combine(projectDirectory, "bin");
                if (!Directory.Exists(binDirectory))
                {
                    continue;
                }

                var match = Directory
                    .EnumerateFiles(binDirectory, exeName, SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();

                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }
}
