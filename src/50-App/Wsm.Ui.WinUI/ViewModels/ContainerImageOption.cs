using Wsm.Hcs.Storage;
using Wsm.Ui.WinUI.Localization;

namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>
/// One entry of the editable image-reference combo box: a catalog image shown
/// with its display name, approximate size and localized description. The
/// combo's editable text is the bare <see cref="Reference"/> (via
/// <see cref="ToString"/>), so picking an entry and typing a custom reference
/// take the same path.
/// </summary>
public sealed record ContainerImageOption
{
    public ContainerImageOption(ContainerImageInfo info, IAppLocalizer localizer)
    {
        Reference = info.Reference;
        DisplayName = info.DisplayName;
        SizeText = $" · ~{info.ApproxSizeGB:0.#} GB";
        Description = localizer[info.DescriptionKey];
    }

    /// <summary>The registry reference imported on download.</summary>
    public string Reference { get; }

    /// <summary>Human-readable image name.</summary>
    public string DisplayName { get; }

    /// <summary>Approximate download size, formatted for display.</summary>
    public string SizeText { get; }

    /// <summary>The localized short description.</summary>
    public string Description { get; }

    /// <summary>The editable combo box shows the reference for a selected entry.</summary>
    public override string ToString() => Reference;
}
