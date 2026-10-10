namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>A selectable UI language. Display names are shown in their own language.</summary>
public sealed record LanguageOption(string CultureName, string DisplayName)
{
    /// <summary>The languages the UI ships with.</summary>
    public static IReadOnlyList<LanguageOption> CreateDefaults() =>
    [
        new LanguageOption("en-US", "English"),
        new LanguageOption("zh-Hans", "简体中文"),
    ];

    /// <summary>Resolves the closest shipped language for a culture name.</summary>
    public static LanguageOption Resolve(string? cultureName)
    {
        var languages = CreateDefaults();
        if (!string.IsNullOrWhiteSpace(cultureName))
        {
            if (cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                return languages[1];
            }

            if (cultureName.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            {
                return languages[0];
            }
        }

        return languages[0];
    }
}
