using System.Text.Json;
using Wsm.Domain.ValueObjects;

namespace Wsm.Persistence;

/// <summary>Small, user-scoped UI preferences (the chosen UI language and the config &amp; data folder).</summary>
public sealed record UiSettings
{
    /// <summary>The chosen UI culture name (for example <c>zh-Hans</c>), or null for the system default.</summary>
    public string? CultureName { get; init; }

    /// <summary>
    /// The user-selectable config &amp; data folder, or null for the default
    /// (<see cref="SandboxPersistenceOptions.DefaultConfigDataFolder"/>).
    /// </summary>
    public string? ConfigDataFolder { get; init; }

    /// <summary>
    /// The global application launch list added by the user (offered as
    /// quick-launch buttons for every sandbox, on top of the built-in defaults
    /// and each sandbox's own additions).
    /// </summary>
    public IReadOnlyList<RailApp> QuickLaunchApps { get; init; } = Array.Empty<RailApp>();

    /// <summary>An empty settings instance (system defaults).</summary>
    public static UiSettings Default { get; } = new();

    /// <summary>
    /// Returns <see cref="ConfigDataFolder"/>, or the default
    /// (<c>%USERPROFILE%\Documents\WsmContainers</c>) when unset.
    /// </summary>
    public string ResolveConfigDataFolder() =>
        string.IsNullOrWhiteSpace(ConfigDataFolder)
            ? SandboxPersistenceOptions.DefaultConfigDataFolder
            : ConfigDataFolder;
}

/// <summary>
/// Loads/saves the UI settings file (<c>ui-settings.json</c>) under
/// <c>%LOCALAPPDATA%\WindowsSandboxManager</c>.
/// <para>
/// This is deliberately static: the WinUI host must apply the persisted culture
/// at process start, <b>before</b> any window or resource lookup and before the
/// ABP container exists. All failures are swallowed and fall back to defaults.
/// </para>
/// </summary>
public static class UiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>The default settings file path under <c>%LOCALAPPDATA%</c>.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsSandboxManager",
        "ui-settings.json");

    /// <summary>Loads the settings, returning <see cref="UiSettings.Default"/> on any failure.</summary>
    public static UiSettings Load(string? path = null)
    {
        try
        {
            var actual = path ?? DefaultPath;
            if (!File.Exists(actual))
            {
                return UiSettings.Default;
            }

            var json = File.ReadAllText(actual);
            return JsonSerializer.Deserialize<UiSettings>(json, JsonOptions) ?? UiSettings.Default;
        }
        catch (IOException)
        {
            return UiSettings.Default;
        }
        catch (JsonException)
        {
            return UiSettings.Default;
        }
        catch (UnauthorizedAccessException)
        {
            return UiSettings.Default;
        }
    }

    /// <summary>Persists the settings, creating the containing folder when needed.</summary>
    public static void Save(UiSettings settings, string? path = null)
    {
        var actual = path ?? DefaultPath;
        var directory = Path.GetDirectoryName(actual);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(actual, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
