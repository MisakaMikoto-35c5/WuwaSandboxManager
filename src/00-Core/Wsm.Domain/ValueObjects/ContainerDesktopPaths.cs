namespace Wsm.Domain.ValueObjects;

/// <summary>
/// The container-side locations of the desktop channel. They are the contract
/// between the three parts that never run in the same process:
/// <list type="bullet">
///   <item>the <b>image payload</b> (<c>C:\wsm\wsm-winlogon-wrap.cmd</c> and
///   <c>C:\wsm\wsm-desktop-read.ps1</c>, installed by the container image
///   customizer),</item>
///   <item>the in-container read side / agent, and</item>
///   <item>the host-side desktop providers.</item>
/// </list>
/// It lives in the domain because both the storage layer (image payload) and the
/// connection layer (providers) must agree on it.
/// </summary>
public static class ContainerDesktopPaths
{
    /// <summary>Directory inside the container that holds the channel's files.</summary>
    public const string ContainerAgentDirectory = @"C:\wsm";

    /// <summary>Shared mirror file published by the window-mirror read side.</summary>
    public const string MirrorFile = @"C:\wsm\mirror.shm";

    /// <summary>Shared frame file published by the frame-buffer agent.</summary>
    public const string FrameFile = @"C:\wsm\frame.shm";

    /// <summary>The logon hook that starts the read side inside the container's session.</summary>
    public const string WrapCommand = @"C:\wsm\wsm-winlogon-wrap.cmd";

    /// <summary>
    /// The persistent shell (<c>Winlogon\Shell</c>) that runs the read side in a restart
    /// loop. A plain <c>cmd.exe</c> exits on EOF and tears the console session down with
    /// it, so the shell must be something that never exits on its own.
    /// </summary>
    public const string ShellCommand = @"C:\wsm\wsm-shell.cmd";

    /// <summary>The window-mirror read side.</summary>
    public const string ReadAgent = @"C:\wsm\wsm-desktop-read.ps1";

    /// <summary>Marker file that makes a running read side exit.</summary>
    public const string StopFile = @"C:\wsm\agent-stop.txt";

    /// <summary>Prefix of the one-file-per-command input queue.</summary>
    public const string InputPrefix = "input-";

    /// <summary>Name of the marker file that makes a running read side or agent exit.</summary>
    public const string StopFileName = "agent-stop.txt";

    /// <summary>
    /// Name of the mode file the provider writes into the agent directory before the
    /// agent starts: <c>pagefile</c> or <c>file</c> (absent = <c>file</c>).
    /// </summary>
    public const string ModeFileName = "shm-mode.txt";

    /// <summary>The image whose execution the logon hook rides on.</summary>
    public const string WinlogonImage = "winlogon.exe";

    /// <summary>
    /// Turns a container-side directory (<c>C:\wsm</c>) into the relative path to
    /// combine with a container root. It must not stay rooted:
    /// <see cref="Path.Combine(string,string)"/> <b>replaces</b> the first segment when
    /// the second is rooted, which points the host at its own <c>C:\wsm</c> instead of
    /// the container's (a bug we shipped once - there is a regression test).
    /// </summary>
    public static string ToRelative(string containerDirectory)
    {
        var relative = containerDirectory.Replace('/', '\\').TrimStart('\\');
        var colon = relative.IndexOf(':');
        if (colon >= 0)
        {
            relative = relative[(colon + 1)..];
        }

        return relative.TrimStart('\\');
    }

    /// <summary>The host-visible path of a container-side path under a container root.</summary>
    public static string ToHostPath(string containerRoot, string containerPath)
        => Path.Combine(containerRoot.TrimEnd('\\'), ToRelative(containerPath));
}
