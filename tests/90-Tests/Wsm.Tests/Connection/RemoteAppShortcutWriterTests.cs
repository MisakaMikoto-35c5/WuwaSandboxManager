using Wsm.Connection.RemoteApp;
using Wsm.Connection.RemoteApp.Internal;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Covers the desktop shortcut writer: the <c>.lnk</c> must run the companion
/// <c>wsm</c> CLI so the connection is resolved live (the guest address and
/// credential change between sandbox starts).
/// </summary>
public sealed class RemoteAppShortcutWriterTests
{
    [Fact]
    public void Build_arguments_points_the_cli_at_the_sandbox_app()
    {
        var sandboxId = Guid.NewGuid();
        var arguments = ShellLinkShortcutWriter.BuildArguments(new RemoteAppShortcutRequest
        {
            LinkPath = @"C:\nowhere\x.lnk",
            CliExecutablePath = @"C:\wsm\wsm.exe",
            SandboxId = sandboxId,
            Program = @"C:\Windows\System32\notepad.exe",
        });

        Assert.Equal(
            $"connect {sandboxId:D} --remoteapp --program \"C:\\Windows\\System32\\notepad.exe\" --quiet",
            arguments);
    }

    [Fact]
    public void Build_arguments_carries_the_program_arguments()
    {
        var sandboxId = Guid.NewGuid();
        var arguments = ShellLinkShortcutWriter.BuildArguments(new RemoteAppShortcutRequest
        {
            LinkPath = @"C:\nowhere\x.lnk",
            CliExecutablePath = @"C:\wsm\wsm.exe",
            SandboxId = sandboxId,
            Program = @"C:\Windows\System32\cmd.exe",
            ProgramArguments = "/k dir",
        });

        Assert.Contains("--program-args \"/k dir\"", arguments);
    }

    [Fact]
    public void Create_writes_a_shell_link()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wsm-shortcut-test-{Guid.NewGuid():N}");
        var linkPath = Path.Combine(directory, "app.lnk");
        try
        {
            new ShellLinkShortcutWriter().Create(new RemoteAppShortcutRequest
            {
                LinkPath = linkPath,
                CliExecutablePath = @"C:\Windows\System32\cmd.exe", // any existing target
                SandboxId = Guid.NewGuid(),
                Program = @"C:\Windows\System32\notepad.exe",
                Description = "Test shortcut",
            });

            Assert.True(File.Exists(linkPath));
            var bytes = File.ReadAllBytes(linkPath);
            Assert.True(bytes.Length > 76);
            // The shell link header: HeaderSize (4C 00 00 00) + CLSID_ShellLink.
            Assert.Equal(0x4C, bytes[0]);
            Assert.Equal(0x00, bytes[1]);
            Assert.Equal(0x00, bytes[2]);
            Assert.Equal(0x00, bytes[3]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
