using Wsm.Application;
using Xunit;

namespace Wsm.Tests.Application;

/// <summary>Covers parsing of the process command line for the open-with path.</summary>
public sealed class LaunchArgumentsTests
{
    [Fact]
    public void Finds_the_manifest_path_among_other_arguments()
    {
        var args = new[] { @"C:\app\Wsm.Ui.WinUI.exe", "--verbose", @"C:\manifests\abc.wsbx" };

        var path = WsmLaunchArguments.GetManifestPath(args);

        Assert.Equal(@"C:\manifests\abc.wsbx", path);
    }

    [Fact]
    public void Is_case_insensitive_on_the_extension()
    {
        Assert.True(WsmLaunchArguments.IsManifestPath(@"C:\manifests\abc.WSBX"));
    }

    [Fact]
    public void Returns_null_when_no_manifest_is_present()
    {
        var args = new[] { @"C:\app\Wsm.Ui.WinUI.exe", "--help" };

        Assert.Null(WsmLaunchArguments.GetManifestPath(args));
    }

    [Fact]
    public void Ignores_switches_and_empty_values()
    {
        Assert.False(WsmLaunchArguments.IsManifestPath("--wsbx"));
        Assert.False(WsmLaunchArguments.IsManifestPath(""));
        Assert.False(WsmLaunchArguments.IsManifestPath(null));
        Assert.Null(WsmLaunchArguments.GetManifestPath(null));
    }
}
