using Microsoft.Win32;
using Wsm.Application;

namespace Wsm.Ui.WinUI;

/// <summary>
/// Registers the <c>.wsm</c> portable sandbox file type under HKCU (no admin
/// required). Idempotent: re-running simply overwrites the same values, and all
/// failures are ignored by the caller.
/// </summary>
internal static class FileAssociationRegistrar
{
    private const string ClassesRoot = @"Software\Classes";

    /// <summary>Writes the extension, ProgID and open command for <c>.wsm</c>.</summary>
    public static void Register(string executablePath)
    {
        using (var extension = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{WsmFileAssociation.Extension}"))
        {
            extension.SetValue(null, WsmFileAssociation.ProgId);
        }

        using (var progId = Registry.CurrentUser.CreateSubKey($@"{ClassesRoot}\{WsmFileAssociation.ProgId}"))
        {
            progId.SetValue(null, WsmFileAssociation.ProgIdDescription);
        }

        using var command = Registry.CurrentUser.CreateSubKey(
            $@"{ClassesRoot}\{WsmFileAssociation.ProgId}\shell\open\command");
        command.SetValue(null, WsmFileAssociation.BuildCommandTemplate(executablePath));
    }
}
