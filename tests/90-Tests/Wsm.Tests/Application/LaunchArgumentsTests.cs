using Wsm.Application;
using Xunit;

namespace Wsm.Tests.Application;

/// <summary>Covers parsing of the process command line for the open-with path.</summary>
public sealed class LaunchArgumentsTests
{
    [Fact]
    public void Finds_the_sandbox_file_path_among_other_arguments()
    {
        var args = new[] { @"C:\app\Wsm.Ui.WinUI.exe", "--verbose", @"D:\Special\Path\Something.wsm" };

        var path = WsmLaunchArguments.GetManifestPath(args);

        Assert.Equal(@"D:\Special\Path\Something.wsm", path);
    }

    [Fact]
    public void Is_case_insensitive_on_the_extension()
    {
        Assert.True(WsmLaunchArguments.IsManifestPath(@"C:\manifests\abc.WSM"));
    }

    [Fact]
    public void Returns_null_when_no_sandbox_file_is_present()
    {
        var args = new[] { @"C:\app\Wsm.Ui.WinUI.exe", "--help" };

        Assert.Null(WsmLaunchArguments.GetManifestPath(args));
    }

    [Fact]
    public void Ignores_switches_and_empty_values()
    {
        Assert.False(WsmLaunchArguments.IsManifestPath("--wsm"));
        Assert.False(WsmLaunchArguments.IsManifestPath(""));
        Assert.False(WsmLaunchArguments.IsManifestPath(null));
        Assert.Null(WsmLaunchArguments.GetManifestPath(null));
    }
}
