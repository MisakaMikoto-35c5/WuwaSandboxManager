using System.Security.Principal;
using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.ContainerDesktop.Internal;

/// <summary>
/// Negotiates the shared-memory mode of a desktop channel
/// (<see cref="ContainerDesktopMode"/>). The provider writes its choice into
/// <c>shm-mode.txt</c> in the agent directory before the agent starts. A reader can
/// subsequently request file mode after passthrough fails; the same writer watches the
/// file, rebuilds its backing and publishes the new mode in the header.
/// <para>
/// Pagefile mode only pays off when the process that <i>maps</i> the section (the
/// viewer) can duplicate a handle out of the in-container writer, which needs
/// elevation. A viewer launched into the interactive session by a SYSTEM service
/// carries the user's (UAC-filtered) token, so a SYSTEM host negotiates
/// <see cref="ContainerDesktopMode.File"/> even though the host itself could do the
/// passthrough.
/// </para>
/// </summary>
internal static class ShmModeNegotiation
{
    /// <summary>Mode token for <see cref="ContainerDesktopMode.Pagefile"/>.</summary>
    public const string PagefileToken = "pagefile";

    /// <summary>Mode token for <see cref="ContainerDesktopMode.File"/>.</summary>
    public const string FileToken = "file";

    /// <summary>
    /// Parses a <c>shm-mode.txt</c> body. Anything that is not explicitly
    /// <c>pagefile</c> - including a missing file - means <see cref="ContainerDesktopMode.File"/>,
    /// which keeps agents started without a mode file (the image's logon hook)
    /// backward compatible.
    /// </summary>
    public static ContainerDesktopMode Parse(string? content)
        => content is not null && content.Trim().Equals(PagefileToken, StringComparison.OrdinalIgnoreCase)
            ? ContainerDesktopMode.Pagefile
            : ContainerDesktopMode.File;

    /// <summary>The <c>shm-mode.txt</c> body for a mode.</summary>
    public static string ToFileContent(ContainerDesktopMode mode)
        => mode == ContainerDesktopMode.Pagefile ? PagefileToken : FileToken;

    /// <summary>
    /// The mode a provider should negotiate.
    /// <paramref name="viewerRunsAsInteractiveUser"/> is
    /// <see cref="ISessionProcessLauncher.CanLaunchIntoInteractiveSession"/>: true when
    /// the host is SYSTEM and the viewer is launched into the interactive session with
    /// the user's (non-elevated) token.
    /// </summary>
    public static ContainerDesktopMode Negotiate(bool hostElevated, bool viewerRunsAsInteractiveUser)
        => hostElevated && !viewerRunsAsInteractiveUser
            ? ContainerDesktopMode.Pagefile
            : ContainerDesktopMode.File;

    /// <summary>True when the current process runs elevated (administrator).</summary>
    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Writes <c>shm-mode.txt</c> into the (host path of the) agent directory.</summary>
    public static void WriteModeFile(string hostAgentDirectory, ContainerDesktopMode mode)
        => File.WriteAllText(
            Path.Combine(hostAgentDirectory, ContainerDesktopPaths.ModeFileName),
            ToFileContent(mode),
            new System.Text.UTF8Encoding(false));
}
