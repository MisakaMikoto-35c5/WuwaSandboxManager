using Wsm.Host.Environment.Internal;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Covers <see cref="SandboxClientResolver"/>: candidate names, candidate
/// locations and precedence, without touching the real host.
/// </summary>
public sealed class SandboxClientResolverTests
{
    private const string WindowsApps = @"C:\Users\u\AppData\Local\Microsoft\WindowsApps";
    private const string Package = @"C:\Program Files\WindowsApps\MicrosoftWindows.WindowsSandbox_1.0_x64__abc";
    private const string System32 = @"C:\Windows\System32";

    [Fact]
    public void Prefers_the_remote_session_alias()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(WindowsApps, "WindowsSandboxRemoteSession.exe"),
            Path.Combine(System32, "WindowsSandboxClient.exe"),
        };

        var resolved = SandboxClientResolver.Resolve(Package, WindowsApps, System32, present.Contains);

        Assert.Equal(Path.Combine(WindowsApps, "WindowsSandboxRemoteSession.exe"), resolved);
    }

    [Fact]
    public void Falls_back_to_the_package_install_directory()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(Package, "WindowsSandboxRemoteSession.exe"),
        };

        var resolved = SandboxClientResolver.Resolve(Package, WindowsApps, System32, present.Contains);

        Assert.Equal(Path.Combine(Package, "WindowsSandboxRemoteSession.exe"), resolved);
    }

    [Fact]
    public void Falls_back_to_system32_client()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(System32, "WindowsSandboxClient.exe"),
        };

        var resolved = SandboxClientResolver.Resolve(Package, WindowsApps, System32, present.Contains);

        Assert.Equal(Path.Combine(System32, "WindowsSandboxClient.exe"), resolved);
    }

    [Fact]
    public void Prefers_remote_session_over_client_across_locations()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(System32, "WindowsSandboxClient.exe"),
            Path.Combine(System32, "WindowsSandboxRemoteSession.exe"),
        };

        var resolved = SandboxClientResolver.Resolve(packageInstallPath: null, WindowsApps, System32, present.Contains);

        Assert.Equal(Path.Combine(System32, "WindowsSandboxRemoteSession.exe"), resolved);
    }

    [Fact]
    public void Returns_null_when_nothing_exists()
    {
        var resolved = SandboxClientResolver.Resolve(Package, WindowsApps, System32, _ => false);

        Assert.Null(resolved);
    }

    [Fact]
    public void Tolerates_a_missing_package_path()
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(WindowsApps, "WindowsSandboxClient.exe"),
        };

        var resolved = SandboxClientResolver.Resolve(null, WindowsApps, System32, present.Contains);

        Assert.Equal(Path.Combine(WindowsApps, "WindowsSandboxClient.exe"), resolved);
    }
}
