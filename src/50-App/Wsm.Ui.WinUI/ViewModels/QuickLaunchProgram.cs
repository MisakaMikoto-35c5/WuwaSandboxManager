namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>
/// A one-click RAIL program offered on the sandbox detail surface. RAIL is
/// RemoteApp-only, so each entry opens the sandbox with that program as its
/// RemoteApp.
/// </summary>
/// <param name="DisplayName">Localized button label.</param>
/// <param name="Path">The guest-side executable, e.g. <c>C:\Windows\explorer.exe</c>.</param>
/// <param name="Glyph">A Segoe MDL2 glyph shown on the button.</param>
public sealed record QuickLaunchProgram(string DisplayName, string Path, string Glyph);
