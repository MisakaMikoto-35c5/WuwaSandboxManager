namespace Wsm.Connection.RdpHost.Internal;

/// <summary>
/// Last-resort session-event capture for the mstscax presentation. The WinUI
/// host has no console, so the ActiveX events (<c>OnDisconnected</c>,
/// <c>OnFatalError</c>, <c>OnLogonError</c>) would leave no trace anywhere;
/// they are appended to <c>rdp-events.log</c> under
/// <c>%LOCALAPPDATA%\WindowsSandboxManager</c>. Never throws.
/// </summary>
internal static class RdpEventLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsSandboxManager",
        "rdp-events.log");

    public static void Write(string eventName, string detail)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {eventName}: {detail}{Environment.NewLine}");
        }
        catch
        {
            // Best effort: diagnostics must never break the session.
        }
    }
}
