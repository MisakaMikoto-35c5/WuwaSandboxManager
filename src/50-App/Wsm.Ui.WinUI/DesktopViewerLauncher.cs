using System.Diagnostics;

namespace Wsm.Ui.WinUI;

/// <summary>
/// Launches a container desktop viewer (<c>wsm desktop-view</c>) as a child
/// process. Viewers are deliberately not tracked: the in-container agent keeps
/// itself alive while at least one viewer lock is live and exits shortly after
/// the last one closes (<c>ViewerLiveness</c>), so a viewer this process loses
/// track of still cleans the channel up.
/// </summary>
internal static class DesktopViewerLauncher
{
    /// <summary>
    /// Starts <c>wsm desktop-view --frame &lt;framePath&gt; --mode &lt;mode&gt;</c>
    /// (<paramref name="mode"/> is <c>frame</c> for the whole desktop or
    /// <c>windows</c> for one host window per container window).
    /// </summary>
    public static void Launch(string cliExePath, string framePath, string mode)
    {
        var startInfo = new ProcessStartInfo(cliExePath)
        {
            // The CLI is a console app; the viewer window it shows is unaffected.
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("desktop-view");
        startInfo.ArgumentList.Add("--frame");
        startInfo.ArgumentList.Add(framePath);
        startInfo.ArgumentList.Add("--mode");
        startInfo.ArgumentList.Add(mode);

        Process.Start(startInfo);
    }
}
