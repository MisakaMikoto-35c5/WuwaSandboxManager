using Wsm.Domain.ValueObjects;

namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>
/// A selectable RDP connector presentation for the app-settings dialog. The two
/// connectors ship as independent modules (WPF and WinUI 3); this is how the user
/// picks which one presents sandbox sessions.
/// </summary>
public sealed class RdpConnectorOption
{
    public RdpConnectorOption(RdpConnectorKind kind, string displayName)
    {
        Kind = kind;
        DisplayName = displayName;
    }

    public RdpConnectorKind Kind { get; }

    public string DisplayName { get; }

    public static IReadOnlyList<RdpConnectorOption> CreateDefaults() =>
    [
        new(RdpConnectorKind.Wpf, "WPF"),
        new(RdpConnectorKind.WinUi, "WinUI 3"),
    ];

    /// <summary>Resolves the persisted kind to an option (defaulting to WPF).</summary>
    public static RdpConnectorOption Resolve(RdpConnectorKind? kind)
    {
        var options = CreateDefaults();
        return options.FirstOrDefault(option => option.Kind == (kind ?? RdpConnectorKind.Wpf))
            ?? options[0];
    }
}
