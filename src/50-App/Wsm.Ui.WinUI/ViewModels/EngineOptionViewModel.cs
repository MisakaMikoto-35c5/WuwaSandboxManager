using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Wsm.Engines.Abstractions;
using Wsm.Ui.WinUI.Localization;

namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>
/// One engine shown in the engine selector: its identity, availability and a
/// compact capability summary. Reserved engines (Windows Container) are always
/// unavailable and rendered as such.
/// </summary>
public sealed partial class EngineOptionViewModel : ObservableObject
{
    private static readonly Color AvailableColor = Color.FromArgb(255, 0x3D, 0xD6, 0x8C);
    private static readonly Color ReservedColor = Color.FromArgb(255, 0xE0, 0xA9, 0x3B);
    private static readonly Color UnavailableColor = Color.FromArgb(255, 0x9A, 0xA0, 0xA6);

    public EngineOptionViewModel(ISandboxEngine engine, IAppLocalizer localizer)
    {
        Name = engine.Name;
        Kind = engine.Kind;
        IsAvailable = engine.IsAvailable;
        IsReserved = engine.Kind == SandboxEngineKind.WindowsContainer;
        Capabilities = engine.Capabilities;
        StatusLabel = IsReserved
            ? localizer["EngineReserved.Text"]
            : IsAvailable
                ? localizer["EngineAvailable.Text"]
                : localizer["EngineUnavailable.Text"];
        CapabilitySummary = BuildSummary(engine.Capabilities, localizer);

        var color = IsReserved ? ReservedColor : IsAvailable ? AvailableColor : UnavailableColor;
        StatusBrush = new SolidColorBrush(color);
        StatusBackgroundBrush = new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B));
    }

    public string Name { get; }

    public SandboxEngineKind Kind { get; }

    public bool IsAvailable { get; }

    public bool IsReserved { get; }

    public SandboxEngineCapabilities Capabilities { get; }

    public string KindLabel => Kind.ToString();

    public string StatusLabel { get; }

    public string CapabilitySummary { get; }

    public Brush StatusBrush { get; }

    public Brush StatusBackgroundBrush { get; }

    [ObservableProperty] private bool _isSelected;

    private static string BuildSummary(SandboxEngineCapabilities caps, IAppLocalizer localizer)
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

        return parts.Count == 0 ? localizer["Cap.None.Text"] : string.Join(" · ", parts);
    }
}
