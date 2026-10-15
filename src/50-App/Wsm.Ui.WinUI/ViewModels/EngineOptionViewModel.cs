using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Wsm.Ui.WinUI.Localization;

namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>
/// One engine shown in the engine selector: its identity, availability and a
/// compact capability summary. The process-isolation engine (Windows Container)
/// is available only when the isolation policy allows it, and its status line
/// explains why (e.g. a Windows Server image or a build mismatch).
/// </summary>
public sealed partial class EngineOptionViewModel : ObservableObject
{
    private static readonly Color AvailableColor = Color.FromArgb(255, 0x3D, 0xD6, 0x8C);
    private static readonly Color UnavailableColor = Color.FromArgb(255, 0x9A, 0xA0, 0xA6);

    public EngineOptionViewModel(ISandboxEngine engine, IAppLocalizer localizer, string? reasonKey = null)
    {
        Name = engine.Name;
        Kind = engine.Kind;
        IsAvailable = engine.IsAvailable;
        IsProcessIsolation = engine.Kind == SandboxEngineKind.WindowsContainer;
        Capabilities = engine.Capabilities;

        Detail = reasonKey is null ? string.Empty : localizer[reasonKey];
        StatusLabel = IsAvailable
            ? localizer["EngineAvailable.Text"]
            : localizer["EngineUnavailable.Text"];

        var capabilities = BuildCapabilityLabels(engine.Capabilities, localizer);
        ShortSummary = IsProcessIsolation && !string.IsNullOrEmpty(Detail)
            ? Detail
            : BuildShortSummary(capabilities, localizer);
        CapabilityTooltip = BuildTooltip(engine.Kind.ToString(), capabilities, Detail, localizer);

        var color = IsAvailable ? AvailableColor : UnavailableColor;
        StatusBrush = new SolidColorBrush(color);
        StatusBackgroundBrush = new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B));
    }

    public string Name { get; }

    public SandboxEngineKind Kind { get; }

    public bool IsAvailable { get; }

    /// <summary>True for the process-isolation (Windows Container) engine.</summary>
    public bool IsProcessIsolation { get; }

    public SandboxEngineCapabilities Capabilities { get; }

    public string StatusLabel { get; }

    /// <summary>The localized reason the engine is (not) available; may be empty.</summary>
    public string Detail { get; }

    /// <summary>A compact one-line summary for the engine card.</summary>
    public string ShortSummary { get; }

    /// <summary>The full capability list, shown on hover instead of on the card.</summary>
    public string CapabilityTooltip { get; }

    public Brush StatusBrush { get; }

    public Brush StatusBackgroundBrush { get; }

    [ObservableProperty] private bool _isSelected;

    private const int SummaryLimit = 3;

    private static IReadOnlyList<string> BuildCapabilityLabels(
        SandboxEngineCapabilities caps,
        IAppLocalizer localizer)
    {
        var parts = new List<string>();
        if (caps.SupportsMultipleInstances) parts.Add(localizer["Cap.Multi.Text"]);
        if (caps.SupportsCpuConfiguration) parts.Add(localizer["Cap.Cpu.Text"]);
        if (caps.SupportsCustomStorageLocation) parts.Add(localizer["Cap.Storage.Text"]);
        if (caps.SupportsCustomBaseImage) parts.Add(localizer["Cap.BaseImage.Text"]);
        if (caps.SupportsCheckpoints) parts.Add(localizer["Cap.Checkpoints.Text"]);
        if (caps.SupportsRuntimeFolderMapping) parts.Add(localizer["Cap.HotMapping.Text"]);
        if (caps.SupportsProcessExecution) parts.Add(localizer["Cap.Exec.Text"]);
        if (caps.RequiresElevation) parts.Add(localizer["Cap.Elevation.Text"]);
        return parts;
    }

    private static string BuildShortSummary(IReadOnlyList<string> capabilities, IAppLocalizer localizer)
    {
        if (capabilities.Count == 0)
        {
            return localizer["Cap.None.Text"];
        }

        var shown = capabilities.Take(SummaryLimit);
        var summary = string.Join(" · ", shown);
        return capabilities.Count > SummaryLimit ? $"{summary} · …" : summary;
    }

    private static string BuildTooltip(string kind, IReadOnlyList<string> capabilities, string detail, IAppLocalizer localizer)
    {
        var body = capabilities.Count == 0 ? localizer["Cap.None.Text"] : string.Join(" · ", capabilities);
        return string.IsNullOrEmpty(detail) ? $"{kind}\n{body}" : $"{kind}\n{body}\n\n{detail}";
    }
}
