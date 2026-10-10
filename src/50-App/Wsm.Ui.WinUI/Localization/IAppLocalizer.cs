using System.Globalization;

namespace Wsm.Ui.WinUI.Localization;

/// <summary>
/// Reads UI strings (the same <c>.resw</c> files the XAML <c>x:Uid</c> markup
/// uses) for code and view models, and owns the active UI culture. Non-UI
/// messages stay on ABP's <c>IStringLocalizer</c>; this covers presentation
/// strings only.
/// </summary>
public interface IAppLocalizer
{
    /// <summary>Looks up a resource by its full <c>.resw</c> key.</summary>
    string this[string key] { get; }

    /// <summary>Looks up and formats a resource with the given arguments.</summary>
    string Get(string key, params object[] args);

    /// <summary>The culture currently applied to the UI.</summary>
    CultureInfo CurrentCulture { get; }

    /// <summary>Raised after <see cref="SetCultureAsync"/> changes the culture.</summary>
    event EventHandler? CultureChanged;

    /// <summary>
    /// Applies a new UI culture to the current and default threads. WinUI has no
    /// hot reload of <c>x:Uid</c> markup, so callers must rebuild the UI
    /// afterwards for XAML strings to refresh.
    /// </summary>
    Task SetCultureAsync(string cultureName);
}
