namespace Wsm.Ui.WinUI;

/// <summary>
/// Last-resort error capture for the GUI: the container's logging only writes
/// to a console the WinUI host does not have, so an exception shown in the
/// status bar would leave no stack trace anywhere. Appends the full exception
/// (type, message, stack) to <c>gui-errors.log</c> next to
/// <c>ui-settings.json</c> under <c>%LOCALAPPDATA%\WindowsSandboxManager</c>.
/// Never throws.
/// </summary>
internal static class GuiErrorLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsSandboxManager",
        "gui-errors.log");

    public static void Write(Exception ex, string context)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best effort: the status bar already carries the message.
        }
    }
}
