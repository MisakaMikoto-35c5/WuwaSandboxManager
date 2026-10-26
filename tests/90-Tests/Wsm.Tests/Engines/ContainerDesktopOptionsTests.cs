using Wsm.Domain.ValueObjects;
using Wsm.Engine.WindowsContainer;
using Wsm.Hcs.Storage;
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
    /// explorer.exe is absent from the Windows Server container images, and a plain
    /// cmd.exe exits on EOF (tearing the console session down with it) - so the default
    /// shell is the persistent WSM shell payload the image customization installs.
    /// </summary>
    [Fact]
    public void The_default_shell_is_the_persistent_wsm_shell()
    {
        var options = new ContainerDesktopOptions();

        Assert.Equal(ContainerDesktopPaths.ShellCommand, options.Shell);
        Assert.Equal(@"C:\wsm\wsm-shell.cmd", options.Shell);
        Assert.Equal(ContainerDesktopSessionDefaults.DefaultUserName, options.UserName);
    }

    /// <summary>
    /// The engine and the image customization must agree on the auto-logon account:
    /// the logon only completes when the baked-in DefaultPassword matches the runtime
    /// password the engine sets with 'net user' (measured, doc section 11.7).
    /// </summary>
    [Fact]
    public void The_desktop_session_defaults_are_shared_with_the_image_customization()
    {
        var options = new ContainerDesktopOptions();

        Assert.Equal("Administrator", options.UserName);
        Assert.False(string.IsNullOrEmpty(ContainerDesktopSessionDefaults.DefaultPassword));
    }

    [Fact]
    public void The_option_alone_enables_the_session()
    {
        Assert.True(new ContainerDesktopOptions { Enabled = true }.ShouldConfigure);
    }

    [Theory]
    [InlineData(ContainerDesktopKind.FrameBuffer, false, false)]
    [InlineData(ContainerDesktopKind.FrameBuffer, true, true)]
    [InlineData(ContainerDesktopKind.WindowMirror, true, true)]
    [InlineData(ContainerDesktopKind.None, true, true)]
    public void The_manifest_explicitly_controls_the_interactive_logon(
        ContainerDesktopKind kind, bool interactiveSession, bool expected)
    {
        var plan = new SandboxDesktopPlan { Kind = kind, InteractiveSession = interactiveSession };

        Assert.Equal(expected, new ContainerDesktopOptions().ShouldConfigureFor(plan));
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

    [Fact]
    public void Sensitive_desktop_commands_are_redacted_before_logging()
    {
        var net = WindowsContainerEngine.RedactCommandForLog(
            "net.exe user \"Administrator\" \"secret-value\"");
        var registry = WindowsContainerEngine.RedactCommandForLog(
            WindowsContainerEngine.RegAdd(
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
                "DefaultPassword",
                "secret-value"));

        Assert.Equal("net.exe user \"Administrator\" \"<redacted>\"", net);
        Assert.DoesNotContain("secret-value", registry);
        Assert.Contains("<redacted>", registry);
        Assert.Equal(
            WindowsContainerEngine.RegAdd("key", "Shell", "cmd.exe"),
            WindowsContainerEngine.RedactCommandForLog(
                WindowsContainerEngine.RegAdd("key", "Shell", "cmd.exe")));
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
