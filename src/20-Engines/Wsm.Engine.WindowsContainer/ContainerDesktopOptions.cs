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
/// </para>
/// <para>
/// <b>Be aware there is no way to see that desktop.</b> RDP is not usable inside a
/// process-isolated container: <c>termsrv</c> refuses to create the RDP listener
/// when it detects a container (<c>WTSIsServerContainer</c> → "TS in container and
/// so Remote Connection is not allow"), and every registry knob that was tried
/// failed. See <c>docs/investigation/13-container-desktop-and-rdp.md</c>. These
/// options only pay off once a viewer exists (an in-session capture agent, or a
/// Hyper-V-isolated container, which is effectively a VM).
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
    public string UserName { get; set; } = "ContainerAdministrator";

    /// <summary>
    /// Password for auto-logon. Without one no auto-logon is configured and the
    /// console session stays on the logon screen — which is itself a desktop
    /// (<c>LogonUI</c>), just not a logged-in one.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// The shell <c>winlogon</c> starts after a logon. The Windows Server
    /// container images ship no <c>explorer.exe</c>, so the default is the shell
    /// that is actually present.
    /// </summary>
    public string Shell { get; set; } = "cmd.exe";

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
