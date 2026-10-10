namespace Wsm.Persistence;

/// <summary>
/// Options for sandbox persistence. <see cref="ConfigDataFolder"/> is the
/// user-selectable config &amp; data folder that holds one portable
/// <c>.wsm</c> sandbox file per sandbox (with its user-data disks beside it).
/// </summary>
public sealed class SandboxPersistenceOptions
{
    /// <summary>
    /// The config &amp; data folder. When unset it falls back to
    /// <see cref="DefaultConfigDataFolder"/> (<c>%USERPROFILE%\Documents\WsmContainers</c>).
    /// </summary>
    public string? ConfigDataFolder { get; set; }

    /// <summary>
    /// The default config &amp; data folder: <c>%USERPROFILE%\Documents\WsmContainers</c>.
    /// Not under <c>C:\ProgramData</c>.
    /// </summary>
    public static string DefaultConfigDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents",
        "WsmContainers");

    /// <summary>Returns <see cref="ConfigDataFolder"/>, or the default when unset.</summary>
    public string ResolveConfigDataFolder() =>
        string.IsNullOrWhiteSpace(ConfigDataFolder) ? DefaultConfigDataFolder : ConfigDataFolder;
}
