using Wsm.Persistence;
using Xunit;

namespace Wsm.Tests.Persistence;

/// <summary>Covers the UI settings file round-trip used for language persistence.</summary>
public sealed class UiSettingsStoreTests
{
    [Fact]
    public void Save_then_load_round_trips_the_culture()
    {
        var path = Path.Combine(Path.GetTempPath(), "wsm-tests-" + Guid.NewGuid(), "ui-settings.json");
        try
        {
            UiSettingsStore.Save(new UiSettings { CultureName = "zh-Hans" }, path);

            var loaded = UiSettingsStore.Load(path);

            Assert.Equal("zh-Hans", loaded.CultureName);
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Load_returns_defaults_when_the_file_is_missing()
    {
        var path = Path.Combine(Path.GetTempPath(), "wsm-tests-" + Guid.NewGuid(), "ui-settings.json");

        var loaded = UiSettingsStore.Load(path);

        Assert.Null(loaded.CultureName);
    }

    [Fact]
    public void Config_data_folder_defaults_under_documents_not_programdata()
    {
        var resolved = UiSettings.Default.ResolveConfigDataFolder();

        Assert.Null(UiSettings.Default.ConfigDataFolder);
        Assert.EndsWith(
            Path.Combine("Documents", "WsmContainers"),
            resolved,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ProgramData", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_then_load_round_trips_the_config_data_folder()
    {
        var path = Path.Combine(Path.GetTempPath(), "wsm-tests-" + Guid.NewGuid(), "ui-settings.json");
        var folder = Path.Combine(Path.GetTempPath(), "wsm-custom-" + Guid.NewGuid());
        try
        {
            UiSettingsStore.Save(new UiSettings { ConfigDataFolder = folder }, path);

            var loaded = UiSettingsStore.Load(path);

            Assert.Equal(folder, loaded.ResolveConfigDataFolder());
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
