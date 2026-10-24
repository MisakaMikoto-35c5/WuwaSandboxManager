using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Hcs.Storage;
using Wsm.Hcs.Storage.Internal;
using Xunit;

namespace Wsm.Tests.Hcs;

/// <summary>
/// Tests for the container base-image customizer. It must find the image's
/// <c>SYSTEM</c> hive in both layer layouts (<c>vhd-with-hives</c> keeps the real
/// file under <c>Files\...\config</c> and hard-links <c>Hives\SYSTEM_BASE</c> to
/// it; a plain extracted layer only has the former), refuse anything that is not
/// a prepared WCOW layer, and write the documented "services + console" count.
/// <para>
/// The patch itself needs elevation (<c>RegLoadKey</c>) and a real hive, so only
/// the parts that do not — path resolution, the constant, and the failures that
/// happen before the hive is touched — are covered here.
/// </para>
/// </summary>
public sealed class ContainerImageCustomizerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wsm-image-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best effort: a locked temp folder must not fail a test run.
        }
    }

    private static readonly ContainerImageCustomizer Customizer =
        new(NullLogger<ContainerImageCustomizer>.Instance);

    /// <summary>
    /// smss creates one initial session per unit: session 0 (services) + one
    /// interactive console session. The container images' active control set
    /// ships <c>1</c>, which leaves winlogon (and any desktop) out entirely.
    /// </summary>
    [Fact]
    public void Interactive_session_count_is_services_plus_console() =>
        Assert.Equal(2, ContainerImageCustomizer.InteractiveSessionCount);

    [Fact]
    public void ResolveSystemHive_prefers_the_files_hive()
    {
        var filesHive = WriteLayerFile("Files", "Windows", "System32", "config", "SYSTEM");
        WriteLayerFile("Hives", "SYSTEM_BASE");

        Assert.Equal(filesHive, ContainerImageCustomizer.ResolveSystemHive(_root));
    }

    [Fact]
    public void ResolveSystemHive_falls_back_to_the_hard_linked_hive()
    {
        // 'vhd-with-hives' layers hard-link Hives\SYSTEM_BASE to the Files hive,
        // but a layer may carry only one of the two paths.
        var hardLink = WriteLayerFile("Hives", "SYSTEM_BASE");

        Assert.Equal(hardLink, ContainerImageCustomizer.ResolveSystemHive(_root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Files")]
    [InlineData(@"Files\Windows")]
    [InlineData(@"Files\Windows\System32\config")]
    public void ResolveSystemHive_rejects_a_folder_that_is_not_a_prepared_layer(string relative)
    {
        Directory.CreateDirectory(Path.Combine(_root, relative));

        Assert.Throws<SandboxValidationException>(() => ContainerImageCustomizer.ResolveSystemHive(_root));
    }

    [Fact]
    public void EnableInteractiveSession_rejects_a_folder_that_is_not_a_prepared_layer()
    {
        Directory.CreateDirectory(_root);

        Assert.Throws<SandboxValidationException>(() => Customizer.EnableInteractiveSession(_root));
    }

    [Fact]
    public void EnableInteractiveSession_reports_the_path_it_rejected()
    {
        Directory.CreateDirectory(_root);

        var exception = Assert.Throws<SandboxValidationException>(
            () => Customizer.EnableInteractiveSession(_root));

        Assert.Contains("WCOW", exception.Message);
    }

    [Fact]
    public void Loading_a_missing_hive_throws_before_touching_the_registry()
    {
        Assert.Throws<SandboxValidationException>(() => OfflineRegistryHive.Load(Path.Combine(_root, "SYSTEM")));
    }

    /// <summary>
    /// The desktop-session defaults are the contract between the offline image patch and
    /// the engine's start-time 'net user': the auto-logon only completes when both sides
    /// configure the same account with the same password.
    /// </summary>
    [Fact]
    public void Desktop_session_options_default_to_the_shared_account_and_password()
    {
        var options = new DesktopSessionOptions();

        Assert.Equal(ContainerDesktopSessionDefaults.DefaultUserName, options.UserName);
        Assert.Equal(ContainerDesktopSessionDefaults.DefaultPassword, options.Password);
        Assert.True(options.InstallWindowMirrorReadSide);
    }

    [Fact]
    public void The_desktop_shell_is_the_persistent_payload_not_cmd()
    {
        // A plain cmd.exe as Winlogon\Shell exits on EOF and tears the console session
        // down with it (measured); the shell payload must be the restart-loop wrapper.
        Assert.Equal(@"C:\wsm\wsm-shell.cmd", ContainerDesktopPaths.ShellCommand);
    }

    [Fact]
    public void EnableDesktopSession_rejects_a_folder_that_is_not_a_prepared_layer()
    {
        Directory.CreateDirectory(_root);

        Assert.Throws<SandboxValidationException>(() => Customizer.EnableDesktopSession(_root));
    }

    /// <summary>Creates a (placeholder, non-hive) file below the temp layer root.</summary>
    private string WriteLayerFile(params string[] relativeParts)
    {
        var path = Path.Combine(new[] { _root }.Concat(relativeParts).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x00]);
        return path;
    }
}
