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
}
