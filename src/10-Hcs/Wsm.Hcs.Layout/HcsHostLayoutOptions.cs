namespace Wsm.Hcs.Layout;

/// <summary>
/// Options for the HCS host layout. The platform containers folder is fixed at
/// <c>C:\ProgramData\Microsoft\Windows\Containers</c> by default (the read-only
/// <c>Layers</c>/<c>BaseImages</c> trees must stay there); the
/// <see cref="SandboxDataFolder"/> — the user-selectable config &amp; data folder —
/// defaults to <c>%USERPROFILE%\Documents\WsmContainers</c>.
/// </summary>
public sealed class HcsHostLayoutOptions
{
    /// <summary>
    /// Platform containers folder. Defaults to
    /// <c>C:\ProgramData\Microsoft\Windows\Containers</c>.
    /// </summary>
    public string? PlatformContainersFolder { get; set; }

    /// <summary>
    /// The config &amp; data folder that holds one subfolder per sandbox. Defaults
    /// to <c>%USERPROFILE%\Documents\WsmContainers</c>.
    /// </summary>
    public string? SandboxDataFolder { get; set; }
}
