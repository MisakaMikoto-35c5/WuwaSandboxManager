namespace Wsm.Application;

/// <summary>
/// Builds the Windows shell association strings for the portable <c>.wsm</c>
/// sandbox format. Pure string logic so it can be unit-tested without touching
/// the registry; the registry write itself lives in the WinUI host.
/// </summary>
public static class WsmFileAssociation
{
    /// <summary>The shell ProgID registered for <c>.wsm</c> files.</summary>
    public const string ProgId = "wsm";

    /// <summary>Human-readable description stored against the ProgID.</summary>
    public const string ProgIdDescription = "Windows Sandbox file";

    /// <summary>The sandbox file extension registered with the shell.</summary>
    public const string Extension = ".wsm";

    /// <summary>
    /// The <c>shell\open\command</c> template written to HKCU, e.g.
    /// <c>"C:\path\Wsm.Ui.WinUI.exe" "%1"</c>.
    /// </summary>
    public static string BuildCommandTemplate(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{executablePath}\" \"%1\"";
    }

    /// <summary>
    /// The concrete command line for opening one sandbox file, e.g.
    /// <c>"C:\path\Wsm.Ui.WinUI.exe" "D:\Special\Path\Something.wsm"</c>.
    /// </summary>
    public static string BuildOpenCommand(string executablePath, string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        return $"\"{executablePath}\" \"{manifestPath}\"";
    }
}
