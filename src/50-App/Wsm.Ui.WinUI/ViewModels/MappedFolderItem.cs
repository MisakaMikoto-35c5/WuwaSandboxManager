using Wsm.Domain.ValueObjects;
using Wsm.Ui.WinUI.Localization;

namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>Presentation wrapper around a <see cref="MappedFolder"/> with a localized access label.</summary>
public sealed class MappedFolderItem
{
    public MappedFolderItem(MappedFolder folder, IAppLocalizer localizer)
    {
        Folder = folder;
        AccessLabel = folder.ReadOnly ? localizer["Folder.ReadOnly.Text"] : localizer["Folder.ReadWrite.Text"];
    }

    public MappedFolder Folder { get; }

    public string HostFolder => Folder.HostFolder;

    public string SandboxFolder => Folder.SandboxFolder;

    public bool ReadOnly => Folder.ReadOnly;

    public string AccessLabel { get; }

    public override string ToString() => Folder.ToString();
}
