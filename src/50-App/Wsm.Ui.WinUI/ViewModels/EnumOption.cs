namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>A localized choice over an enum value, for use in combo boxes.</summary>
public sealed record EnumOption<T>(T Value, string DisplayName)
    where T : struct, Enum
{
    public override string ToString() => DisplayName;
}
