using System.Runtime.InteropServices;

namespace Wsm.Cli;

/// <summary>
/// Win32 helpers for the <c>--quiet</c> mode the desktop shortcuts use: the
/// console is hidden (the session is presented by <c>mstsc</c>, not by this
/// process), and failures are surfaced in a message box because a hidden
/// console cannot show them.
/// </summary>
internal static class ConsoleWindow
{
    private const int SwHide = 0;
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbSetForeground = 0x00010000;

    /// <summary>Hides this process's console window (best effort).</summary>
    public static void Hide()
    {
        try
        {
            var handle = GetConsoleWindow();
            if (handle != IntPtr.Zero)
            {
                ShowWindow(handle, SwHide);
            }
        }
        catch
        {
            // Hiding is cosmetic; never fatal.
        }
    }

    /// <summary>Shows a blocking error message box.</summary>
    public static void ShowError(string message)
    {
        try
        {
            MessageBoxW(
                IntPtr.Zero,
                message,
                "Windows Sandbox Manager",
                MbOk | MbIconError | MbSetForeground);
        }
        catch
        {
            // If even the message box fails there is nothing left to do.
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
