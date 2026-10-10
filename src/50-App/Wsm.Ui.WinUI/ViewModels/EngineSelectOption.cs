using Wsm.Domain.ValueObjects;

namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>
/// One entry in the creation dialog's engine picker. The automatic default is
/// represented with a <c>null</c> <see cref="Kind"/>. Unavailable and reserved
/// engines are listed for transparency but disabled.
/// </summary>
public sealed class EngineSelectOption
{
    public EngineSelectOption(SandboxEngineKind? kind, string displayName, bool isEnabled)
    {
        Kind = kind;
        DisplayName = displayName;
        IsEnabled = isEnabled;
    }

    /// <summary>The engine kind, or <c>null</c> for automatic selection.</summary>
    public SandboxEngineKind? Kind { get; }

    public string DisplayName { get; }

    public bool IsEnabled { get; }
}
