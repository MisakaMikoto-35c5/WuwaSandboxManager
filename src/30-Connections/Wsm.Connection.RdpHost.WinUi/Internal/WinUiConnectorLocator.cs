namespace Wsm.Connection.RdpHost.WinUi.Internal;

/// <summary>
/// Locates the WinUI 3 connector executable (<c>Wsm.Rdp.WinUI.exe</c>): next to
/// the current process first (a deployed bundle), then — for a developer layout
/// — the newest build under the repository's <c>src/50-App/Wsm.Rdp.WinUI/bin</c>
/// found while walking up towards the repository root. Mirrors
/// <c>WsmCliLocator</c>.
/// </summary>
internal static class WinUiConnectorLocator
{
    public static string? Find()
    {
        const string exeName = "Wsm.Rdp.WinUI.exe";

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
                         Path.Combine(current.FullName, "src", "50-App", "Wsm.Rdp.WinUI"),
                         Path.Combine(current.FullName, "50-App", "Wsm.Rdp.WinUI"),
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
