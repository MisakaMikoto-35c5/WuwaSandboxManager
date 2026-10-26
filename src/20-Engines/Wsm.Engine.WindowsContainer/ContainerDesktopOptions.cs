namespace Wsm.Engine.WindowsContainer;

/// <summary>
/// Opt-in <b>desktop session</b> for a process-isolated container, plus the
/// experimental TCP-RDP attempt.
/// <para>
/// A container has no desktop unless its image boots an interactive console
/// session. That is an <i>image</i> property:
/// <see cref="Wsm.Hcs.Storage.IContainerImageCustomizer.EnableInteractiveSession"/>
/// (or <c>wsm image desktop</c>) sets
/// <c>Session Manager\NumberOfInitialSessions=2</c>, so <c>smss.exe</c> creates
/// the console session and <c>winlogon.exe</c>/<c>LogonUI.exe</c> start. This
/// type then decides what happens <i>after</i> a logon (the shell, auto-logon).
/// The one-command image patch that does both sides is <c>wsm image session</c>
/// (<see cref="Wsm.Hcs.Storage.IContainerImageCustomizer.EnableDesktopSession"/>).
/// </para>
/// <para>
/// RDP is not usable inside a process-isolated container: <c>termsrv</c> refuses
/// to create its listener when it detects a container. These options configure the
/// interactive logon used by the in-session capture agent; the frame-buffer and
/// window-mirror providers present those pixels in the user's console session.
/// See <c>docs/investigation/13-container-desktop-and-rdp.md</c>.
/// </para>
/// </summary>
public sealed class ContainerDesktopOptions
{
    /// <summary>Turns <see cref="Enabled"/> on without a config file (values <c>1</c>/<c>true</c>/<c>yes</c>).</summary>
    public const string EnableVariable = "WSM_CONTAINER_DESKTOP";

    /// <summary>Turns <see cref="AttemptTcpRdp"/> on without a config file.</summary>
    public const string AttemptTcpRdpVariable = "WSM_CONTAINER_RDP";

    /// <summary>Whether the engine configures the container's desktop session.</summary>
    public bool Enabled { get; set; }

    /// <summary>The account auto-logon signs in as (containers' built-in admin).</summary>
    public string UserName { get; set; } = Wsm.Hcs.Storage.ContainerDesktopSessionDefaults.DefaultUserName;

    /// <summary>
    /// Password for auto-logon. It must match the <c>DefaultPassword</c> baked into the
    /// image (the engine sets it on the account with <c>net user</c> at start, so the
    /// two agree). Null derives a credential from the resolved base-image path through
    /// <see cref="Wsm.Hcs.Storage.ContainerDesktopSessionDefaults.GetPassword"/>.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// The shell <c>winlogon</c> starts after a logon. The Windows Server
    /// container images ship no <c>explorer.exe</c>, and a plain <c>cmd.exe</c> exits
    /// on EOF and tears the console session down with it - so the default is the
    /// persistent WSM shell, which runs the window-mirror read side in a restart loop.
    /// </summary>
    public string Shell { get; set; } = Wsm.Domain.ValueObjects.ContainerDesktopPaths.ShellCommand;

    /// <summary>
    /// Whether to also attempt the RDP listener (<c>RDP-Tcp</c> on 3389).
    /// <para>
    /// <b>Experimental, and known not to work.</b> It is kept so this branch
    /// records every knob that was tried (the settings the Windows Sandbox guest
    /// uses, which is a <i>client VM</i> where <c>termsrv</c> never enters its
    /// container path). Enabling it costs a few registry writes and a TermService
    /// start; the engine then probes 3389 from the host and logs that the listener
    /// did not come up.
    /// </para>
    /// </summary>
    public bool AttemptTcpRdp { get; set; }

    /// <summary>The port the experimental RDP attempt targets.</summary>
    internal const int RdpPort = 3389;

    /// <summary>True when the desktop session should be configured (option or env var).</summary>
    internal bool ShouldConfigure => Enabled || IsTruthy(Environment.GetEnvironmentVariable(EnableVariable));

    /// <summary>True when the global switch or this sandbox's manifest requests interactive logon.</summary>
    internal bool ShouldConfigureFor(Wsm.Domain.ValueObjects.SandboxDesktopPlan desktop)
        => ShouldConfigure || desktop.InteractiveSession;

    /// <summary>True when the experimental RDP attempt should run (option or env var).</summary>
    internal bool ShouldAttemptTcpRdp =>
        AttemptTcpRdp || IsTruthy(Environment.GetEnvironmentVariable(AttemptTcpRdpVariable));

    private static bool IsTruthy(string? value) =>
        value is not null
        && !value.Equals("0", StringComparison.OrdinalIgnoreCase)
        && !value.Equals("false", StringComparison.OrdinalIgnoreCase)
        && !value.Equals("no", StringComparison.OrdinalIgnoreCase)
        && value.Length > 0;
}
