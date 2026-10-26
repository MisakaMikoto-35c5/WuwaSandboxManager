using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Hcs.Storage;
using Wsm.Hcs.Storage.Internal;
using Xunit;

namespace Wsm.Tests.Hcs;

[CollectionDefinition("Desktop key environment", DisableParallelization = true)]
public sealed class DesktopKeyEnvironmentCollection
{
}

/// <summary>
/// Tests for the container base-image customizer. It must find the image's
/// <c>SYSTEM</c> hive in both layer layouts (<c>vhd-with-hives</c> keeps the real
/// file under <c>Files\...\config</c> and hard-links <c>Hives\SYSTEM_BASE</c> to
/// it; a plain extracted layer only has the former), refuse anything that is not
/// a prepared WCOW layer, and write the documented "services + console" count.
/// <para>
/// The registry patch itself needs elevation (<c>RegLoadKey</c>) and a real hive, so these
/// tests cover path resolution, credential derivation, payload installation, and execution
/// of the embedded shell with all paths redirected to temporary directories.
/// </para>
/// </summary>
[Collection("Desktop key environment")]
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
    public void Desktop_session_options_default_to_the_shared_account_without_an_embedded_password()
    {
        var options = new DesktopSessionOptions();

        Assert.Equal(ContainerDesktopSessionDefaults.DefaultUserName, options.UserName);
        Assert.True(options.Password is null, "The default password must be derived per image, not embedded.");
        Assert.True(options.InstallWindowMirrorReadSide);
    }

    [Fact]
    public void Derived_desktop_password_is_stable_for_the_normalized_image_path()
    {
        var keyPath = Path.Combine(_root, "secrets", "container-desktop.key");
        var layerPath = Path.Combine(_root, "layer");

        WithDesktopKey(keyPath, () =>
        {
            var first = ContainerDesktopSessionDefaults.GetPassword(layerPath);
            AllowCurrentUser(keyPath);
            var keyLength = File.ReadAllBytes(keyPath).Length;
            var second = ContainerDesktopSessionDefaults.GetPassword(Path.Combine(layerPath, "."));

            Assert.True(first.Length >= 32, "The derived password must carry the full HMAC output.");
            Assert.Equal(32, keyLength);
            Assert.True(
                string.Equals(first, second, StringComparison.Ordinal),
                "Equivalent image paths must derive the same password.");
        });
    }

    [Fact]
    public void Derived_desktop_password_changes_with_the_image_path()
    {
        var keyPath = Path.Combine(_root, "secrets", "container-desktop.key");

        WithDesktopKey(keyPath, () =>
        {
            var first = ContainerDesktopSessionDefaults.GetPassword(Path.Combine(_root, "layer-a"));
            AllowCurrentUser(keyPath);
            var second = ContainerDesktopSessionDefaults.GetPassword(Path.Combine(_root, "layer-b"));

            Assert.False(string.Equals(first, second, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Derived_desktop_password_changes_with_the_random_host_key()
    {
        var first = string.Empty;
        var second = string.Empty;
        var firstKey = Path.Combine(_root, "first", "container-desktop.key");
        var secondKey = Path.Combine(_root, "second", "container-desktop.key");
        var layerPath = Path.Combine(_root, "layer");

        WithDesktopKey(firstKey, () => first = ContainerDesktopSessionDefaults.GetPassword(layerPath));
        WithDesktopKey(secondKey, () => second = ContainerDesktopSessionDefaults.GetPassword(layerPath));

        Assert.False(string.Equals(first, second, StringComparison.Ordinal));
    }

    [Fact]
    public void Desktop_key_directory_and_file_allow_only_administrators_and_system()
    {
        var keyPath = Path.Combine(_root, "secrets", "container-desktop.key");

        WithDesktopKey(keyPath, () =>
        {
            _ = ContainerDesktopSessionDefaults.GetPassword(Path.Combine(_root, "layer"));

            var directory = new DirectoryInfo(Path.GetDirectoryName(keyPath)!).GetAccessControl();
            var file = new FileInfo(keyPath).GetAccessControl();
            Assert.True(
                HasOnlyAdministratorAndSystemAccess(directory) && HasOnlyAdministratorAndSystemAccess(file),
                "The key and its directory must disable inheritance and have only Administrators and SYSTEM ACEs.");
        });
    }

    [Fact]
    public void The_desktop_shell_is_the_persistent_payload_not_cmd()
    {
        // A plain cmd.exe as Winlogon\Shell exits on EOF and tears the console session
        // down with it (measured); the shell payload must be the restart-loop wrapper.
        Assert.Equal(@"C:\wsm\wsm-shell.cmd", ContainerDesktopPaths.ShellCommand);
    }

    [Fact]
    public void Desktop_logon_derives_a_missing_password_from_the_image()
    {
        var keyPath = Path.Combine(_root, "secrets", "container-desktop.key");

        WithDesktopKey(keyPath, () =>
        {
            var expected = ContainerDesktopSessionDefaults.GetPassword(_root);
            AllowCurrentUser(keyPath);
            var writes = new List<(string Name, string Value, string Type)>();

            ContainerImageCustomizer.WriteDesktopLogon(
                new DesktopSessionOptions(),
                _root,
                (name, value, type) => writes.Add((name, value, type)));

            var actual = writes.Single(write => write.Name == "DefaultPassword").Value;
            Assert.True(
                string.Equals(expected, actual, StringComparison.Ordinal),
                "The customizer must bake the password derived from this image path.");
        });
    }

    [Fact]
    public void Desktop_logon_keeps_an_explicit_password()
    {
        const string explicitPassword = "test-only-explicit-password";
        var keyPath = Path.Combine(_root, "secrets", "container-desktop.key");

        WithDesktopKey(keyPath, () =>
        {
            var writes = new List<(string Name, string Value, string Type)>();
            ContainerImageCustomizer.WriteDesktopLogon(
                new DesktopSessionOptions(Password: explicitPassword),
                _root,
                (name, value, type) => writes.Add((name, value, type)));

            var actual = writes.Single(write => write.Name == "DefaultPassword").Value;
            Assert.True(actual.SequenceEqual(explicitPassword));
            Assert.False(File.Exists(keyPath));
        });
    }

    [Fact]
    public void Desktop_session_enables_autologon_for_every_boot()
    {
        var writes = new List<(string Name, string Value, string Type)>();

        ContainerImageCustomizer.WriteDesktopLogon(
            new DesktopSessionOptions(Password: "test-only-password"),
            _root,
            (name, value, type) => writes.Add((name, value, type)));

        Assert.Contains(writes, write => write is
        {
            Name: "AutoLogonCount",
            Value: "99",
            Type: "REG_SZ",
        });
        Assert.DoesNotContain(writes, write => write.Name == "AutoLogonCount" && write.Value == "0");
    }

    [Fact]
    public void EnableDesktopSession_rejects_a_folder_that_is_not_a_prepared_layer()
    {
        Directory.CreateDirectory(_root);

        Assert.Throws<SandboxValidationException>(() => Customizer.EnableDesktopSession(_root));
    }

    [Fact]
    public void Interactive_shell_payload_does_not_require_the_read_side()
    {
        Customizer.InstallShellPayload(_root, out var containerPaths);

        Assert.True(File.Exists(Path.Combine(_root, "Files", "wsm", "wsm-shell.cmd")));
        Assert.False(File.Exists(Path.Combine(_root, "Files", "wsm", "wsm-desktop-read.ps1")));
        Assert.Equal(new[] { ContainerDesktopPaths.ShellCommand }, containerPaths);
    }

    [Fact]
    public void Window_mirror_payload_includes_the_default_shell()
    {
        Customizer.InstallWindowMirrorPayload(_root, out var containerPaths);

        Assert.True(File.Exists(Path.Combine(_root, "Files", "wsm", "wsm-shell.cmd")));
        Assert.True(File.Exists(Path.Combine(_root, "Files", "wsm", "wsm-desktop-read.ps1")));
        Assert.Contains(ContainerDesktopPaths.ShellCommand, containerPaths);
    }

    [Fact]
    public async Task Shell_exits_immediately_when_the_stop_file_already_exists()
    {
        var script = WriteShellScript();
        await File.WriteAllTextAsync(Path.Combine(_root, "wsm", "agent-stop.txt"), "stop");
        using var process = StartShell(script);
        try
        {
            Assert.True(
                await ExitsWithin(process, TimeSpan.FromSeconds(5)),
                "The shell must check the stop file before launching another command.");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            StopShell(process);
        }
    }

    [Fact]
    public async Task Shell_without_the_read_side_waits_in_powershell_and_exits_on_stop()
    {
        var script = WriteShellScript();
        using var process = StartShell(script);
        try
        {
            await Task.Delay(750);
            Assert.False(process.HasExited, "The no-mirror shell must stay alive without the read-side payload.");

            await File.WriteAllTextAsync(Path.Combine(_root, "wsm", "agent-stop.txt"), "stop");

            Assert.True(
                await ExitsWithin(process, TimeSpan.FromSeconds(10)),
                "The no-mirror shell must exit after its persistent PowerShell wait observes the stop file.");
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            StopShell(process);
        }
    }

    private string WriteShellScript()
    {
        var directory = Path.Combine(_root, "wsm");
        Directory.CreateDirectory(directory);
        const string resource = "Wsm.Hcs.Storage.Images.wsm-shell.cmd";
        using var stream = typeof(ContainerImageCustomizer).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded payload '{resource}'.");
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd().Replace(@"C:\wsm", directory, StringComparison.OrdinalIgnoreCase);
        var path = Path.Combine(directory, "wsm-shell.cmd");
        File.WriteAllText(path, script);
        return path;
    }

    private static Process StartShell(string script)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("cmd.exe", $"/d /c \"{script}\"")
            {
                WorkingDirectory = Path.GetDirectoryName(script)!,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start the temporary shell payload.");
        }

        return process;
    }

    private static async Task<bool> ExitsWithin(Process process, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            StopShell(process);
            return false;
        }
    }

    private static void StopShell(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
    }

    private static void WithDesktopKey(string keyPath, Action body)
    {
        var original = Environment.GetEnvironmentVariable("WSM_CONTAINER_DESKTOP_KEY");
        try
        {
            Environment.SetEnvironmentVariable("WSM_CONTAINER_DESKTOP_KEY", keyPath);
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable("WSM_CONTAINER_DESKTOP_KEY", original);
            AllowCurrentUser(keyPath);
        }
    }

    private static void AllowCurrentUser(string keyPath)
    {
        var directory = Path.GetDirectoryName(keyPath)!;
        var current = WindowsIdentity.GetCurrent().User!;
        if (Directory.Exists(directory))
        {
            var security = new DirectorySecurity();
            security.AddAccessRule(new FileSystemAccessRule(
                current,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }

        if (File.Exists(keyPath))
        {
            var security = new FileSecurity();
            security.AddAccessRule(new FileSystemAccessRule(
                current, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(keyPath).SetAccessControl(security);
        }
    }

    private static bool HasOnlyAdministratorAndSystemAccess(CommonObjectSecurity security)
    {
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        var expected = new[]
        {
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        };
        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        return rules.Length == expected.Length && rules.All(rule =>
            rule.AccessControlType == AccessControlType.Allow
            && rule.FileSystemRights == FileSystemRights.FullControl
            && expected.Contains((SecurityIdentifier)rule.IdentityReference));
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
