namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>
/// A one-click RAIL program offered on the sandbox detail surface. RAIL is
/// RemoteApp-only, so each entry opens the sandbox with that program as its
/// RemoteApp; the entries come from the built-in defaults, the global launch
/// list (<c>ui-settings.json</c>) and the sandbox's own list.
/// </summary>
/// <param name="DisplayName">Localized button label.</param>
/// <param name="Path">The guest-side executable, e.g. <c>C:\Windows\explorer.exe</c>.</param>
/// <param name="Glyph">A Segoe MDL2 glyph shown on the button.</param>
/// <param name="Arguments">Optional command line passed to the RemoteApp.</param>
/// <param name="IsBuiltIn">True for the fixed defaults, false for user entries.</param>
public sealed record QuickLaunchProgram(
    string DisplayName,
    string Path,
    string Glyph,
    string? Arguments = null,
    bool IsBuiltIn = false);
