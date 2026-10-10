using Wsm.Application;
using Xunit;

namespace Wsm.Tests.Application;

/// <summary>Covers the pure shell-association command strings for <c>.wsm</c>.</summary>
public sealed class FileAssociationTests
{
    [Fact]
    public void Command_template_quotes_the_executable_and_uses_the_percent_one_placeholder()
    {
        var command = WsmFileAssociation.BuildCommandTemplate(@"C:\Program Files\Wsm\Wsm.Ui.WinUI.exe");

        Assert.Equal("\"C:\\Program Files\\Wsm\\Wsm.Ui.WinUI.exe\" \"%1\"", command);
    }

    [Fact]
    public void Open_command_quotes_both_the_executable_and_the_sandbox_file()
    {
        var command = WsmFileAssociation.BuildOpenCommand(@"C:\app\Wsm.Ui.WinUI.exe", @"D:\Special\Path\Something.wsm");

        Assert.Equal("\"C:\\app\\Wsm.Ui.WinUI.exe\" \"D:\\Special\\Path\\Something.wsm\"", command);
    }

    [Fact]
    public void Association_constants_match_the_documented_shell_values()
    {
        Assert.Equal(".wsm", WsmFileAssociation.Extension);
        Assert.Equal("wsm", WsmFileAssociation.ProgId);
        Assert.Equal("Windows Sandbox file", WsmFileAssociation.ProgIdDescription);
    }
}
