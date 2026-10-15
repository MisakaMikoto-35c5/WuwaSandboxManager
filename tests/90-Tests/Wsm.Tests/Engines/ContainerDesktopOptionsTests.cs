using Wsm.Engine.WindowsContainer;
using Xunit;

namespace Wsm.Tests.Engines;

/// <summary>
/// Tests for the opt-in container desktop session: it must be off by default, use
/// a shell the container images actually ship (<c>cmd.exe</c>, not the absent
/// <c>explorer.exe</c>), be switchable from the environment the way the image
/// override is, and produce the exact <c>reg.exe</c> command line the engine
/// sends into the container.
/// </summary>
public sealed class ContainerDesktopOptionsTests
{
    [Fact]
    public void The_desktop_session_is_off_by_default()
    {
        var options = new ContainerDesktopOptions();

        Assert.False(options.Enabled);
        Assert.False(options.ShouldConfigure);
    }

    [Fact]
    public void The_experimental_rdp_attempt_is_off_by_default()
    {
        var options = new ContainerDesktopOptions();

        Assert.False(options.AttemptTcpRdp);
        Assert.False(options.ShouldAttemptTcpRdp);
    }

    /// <summary>
    /// explorer.exe is absent from the Windows Server container images, so
    /// winlogon's default shell could never start.
    /// </summary>
    [Fact]
    public void The_default_shell_is_present_in_the_images()
    {
        var options = new ContainerDesktopOptions();

        Assert.Equal("cmd.exe", options.Shell);
        Assert.Equal("ContainerAdministrator", options.UserName);
    }

    [Fact]
    public void The_option_alone_enables_the_session()
    {
        Assert.True(new ContainerDesktopOptions { Enabled = true }.ShouldConfigure);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("anything")]
    public void A_truthy_environment_value_enables_the_session(string value)
    {
        WithEnvironment(ContainerDesktopOptions.EnableVariable, value, () =>
        {
            Assert.True(new ContainerDesktopOptions().ShouldConfigure);
            Assert.False(new ContainerDesktopOptions().ShouldAttemptTcpRdp);
        });
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("")]
    [InlineData(null)]
    public void A_falsy_or_absent_environment_value_leaves_it_off(string? value)
    {
        WithEnvironment(ContainerDesktopOptions.EnableVariable, value, () =>
            Assert.False(new ContainerDesktopOptions().ShouldConfigure));
    }

    [Fact]
    public void The_rdp_attempt_has_its_own_documented_switch()
    {
        WithEnvironment(ContainerDesktopOptions.AttemptTcpRdpVariable, "1", () =>
            Assert.True(new ContainerDesktopOptions().ShouldAttemptTcpRdp));
    }

    [Fact]
    public void Engine_options_carry_a_desktop_section()
    {
        var options = new WindowsContainerEngineOptions();

        Assert.NotNull(options.Desktop);
        Assert.Equal(ContainerDesktopOptions.RdpPort, 3389);
    }

    [Fact]
    public void RegAdd_builds_the_command_line_sent_into_the_container()
    {
        var command = WindowsContainerEngine.RegAdd(
            @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "Shell", "cmd.exe");

        Assert.Equal(
            @"reg.exe add ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"" /v Shell " +
            @"/t REG_SZ /d ""cmd.exe"" /f",
            command);
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the environment variable set, restoring
    /// the previous value afterwards (the switches deliberately read the
    /// environment the way the image override does).
    /// </summary>
    private static void WithEnvironment(string name, string? value, Action body)
    {
        var original = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, value);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }
}
