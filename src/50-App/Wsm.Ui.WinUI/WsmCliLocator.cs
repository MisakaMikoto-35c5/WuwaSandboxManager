namespace Wsm.Ui.WinUI;

/// <summary>
/// Locates the companion <c>wsm.exe</c> CLI used to host RDP session windows:
/// next to this executable first (a deployed bundle), then — for a developer
/// layout — the newest build under the repository's <c>src/50-App/Wsm.Cli/bin</c>
/// found while walking up towards the repository root. Mirrors
/// <c>CliApplication.FindServiceHostExe</c>.
/// </summary>
internal static class WsmCliLocator
{
    public static string? Find()
    {
        const string exeName = "wsm.exe";

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var directory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                var beside = Path.Combine(directory, exeName);
                if (File.Exists(beside))
                {
                    return beside;
                }
            }
        }

        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            foreach (var projectDirectory in new[]
                     {
                         Path.Combine(current.FullName, "src", "50-App", "Wsm.Cli"),
                         Path.Combine(current.FullName, "50-App", "Wsm.Cli"),
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
