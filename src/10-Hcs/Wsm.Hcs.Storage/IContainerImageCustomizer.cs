namespace Wsm.Hcs.Storage;

/// <summary>
/// Applies WSM-specific changes to an imported WCOW <b>base image layer</b> —
/// changes that have to be baked into the image's registry hives, because a
/// process-isolated container reads them before any WSM code can run inside it.
/// <para>
/// An HCS Argon container's registry <i>is</i> the image's hives (unioned through
/// WCIFS), so nothing can be set from outside once <c>smss.exe</c> is running.
/// </para>
/// </summary>
public interface IContainerImageCustomizer
{
    /// <summary>
    /// Makes a prepared base image layer boot an interactive <b>console</b>
    /// session, by setting
    /// <c>ControlSet*\Control\Session Manager\NumberOfInitialSessions</c> to
    /// <c>2</c> in every control set of the image's <c>SYSTEM</c> hive.
    /// <para>
    /// <c>NumberOfInitialSessions</c> tells <c>smss.exe</c> how many initial
    /// sessions to create: <c>2</c> = the services session <b>and</b> the
    /// interactive console session, <c>1</c> = services only. Microsoft's
    /// container images ship <c>1</c> in their <i>active</i> control set (the
    /// sibling control set still has the default <c>2</c>), so no console session
    /// is ever created and <c>winlogon.exe</c> — and with it <c>LogonUI</c>, the
    /// logon desktop and the shell chain — never starts. A container with no
    /// console session has no desktop at all, which is why
    /// <c>winlogon</c>/<c>userinit</c> are absent from <c>tasklist</c>.
    /// </para>
    /// <para>
    /// Raising it is <b>not</b> sufficient for a remote desktop: RDP itself stays
    /// unavailable, because <c>termsrv</c> refuses to create a listener when it
    /// detects a container. See
    /// <c>docs/investigation/13-container-desktop-and-rdp.md</c>.
    /// </para>
    /// <para>
    /// Requires elevation (the hive is loaded with <c>RegLoadKey</c>) and the hive
    /// file must not be in use — stop any container built from this image and
    /// unprepare/deactivate the layer first.
    /// </para>
    /// </summary>
    /// <returns>What was changed, for logging/telemetry.</returns>
    ContainerImageSessionPrep EnableInteractiveSession(string layerPath);

    /// <summary>
    /// Installs the <b>window-mirror</b> payload into a prepared base image layer: the
    /// logon hook (<c>C:\wsm\wsm-winlogon-wrap.cmd</c>) is appended to
    /// <c>Winlogon\Userinit</c> in the image's <c>SOFTWARE</c> hive, and the read side
    /// (<c>C:\wsm\wsm-desktop-read.ps1</c> + its launcher) is copied into
    /// <c>Files\wsm\</c>.
    /// <para>
    /// This is what makes the read side run <b>inside the container's own interactive
    /// session</b>, which is the only place the container's windows can be enumerated
    /// and captured: from outside, window stations are not addressable and creating a
    /// process in the session fails at DLL initialization (<c>0xC0000142</c>). Measured;
    /// see <c>docs/investigation/13-container-desktop-and-rdp.md</c> sections 16-18.
    /// </para>
    /// <para>
    /// <c>Userinit</c> is used rather than replacing <c>winlogon.exe</c> (e.g. through
    /// Image File Execution Options) because it is the logon extension point Microsoft
    /// documents for this, and it cannot break a logon: the hook only starts the read
    /// side detached and returns immediately.
    /// </para>
    /// <para>
    /// The change is baked into the image, so a container that is already running was
    /// started before it and must be restarted to pick it up. Requires elevation.
    /// </para>
    /// </summary>
    /// <returns>What was changed, for logging/telemetry.</returns>
    ContainerImageDesktopPrep EnableWindowMirror(
        string layerPath, ContainerImageDesktopOptions? options = null);

    /// <summary>
    /// The full <b>desktop session</b> unlock, in one call: the console-session patch
    /// (<see cref="EnableInteractiveSession"/>), auto-start for <c>TermService</c> and
    /// <c>SessionEnv</c> in every control set (winlogon's interactive logon is arbitrated
    /// by the LocalSessionManager inside <c>TermService</c>; the stock images ship it
    /// disabled, so the logon never completes and the console session is torn down again),
    /// auto-logon as
    /// <see cref="DesktopSessionOptions.UserName"/> with a known password
    /// (<c>AutoAdminLogon</c>/<c>DefaultUserName</c>/<c>DefaultPassword</c>/
    /// <c>ForceAutoLogon</c> in <c>Winlogon</c>, plus <c>Lsa\LimitBlankPasswordUse = 0</c>
    /// in every control set), and <c>Winlogon\Shell</c> pointed at
    /// <c>C:\wsm\wsm-shell.cmd</c> - a wrapper that runs the read side in a restart loop,
    /// because a shell that exits (plain <c>cmd.exe</c> reads EOF and quits) tears the
    /// console session down with it.
    /// <para>
    /// A known password is required, not optional: auto-logon with a blank password is
    /// refused, and the logon only completes when the baked-in <c>DefaultPassword</c>
    /// matches the account's runtime password (the engine sets it with <c>net user</c>
    /// at container start; both sides default to
    /// <see cref="ContainerDesktopSessionDefaults.DefaultPassword"/>).
    /// </para>
    /// <para>
    /// Requires elevation; stop any container built from this image first. A container
    /// that is already running was started before the patch and must be restarted.
    /// </para>
    /// </summary>
    /// <returns>What was changed, for logging/telemetry.</returns>
    ContainerImageDesktopSessionPrep EnableDesktopSession(
        string layerPath, DesktopSessionOptions? options = null);
}

/// <summary>Optional logon settings for <see cref="IContainerImageCustomizer.EnableWindowMirror"/>.</summary>
/// <param name="Shell">
/// The shell <c>winlogon</c> starts after logon. Images ship no
/// <c>explorer.exe</c>, so point this at something that exists (e.g. <c>cmd.exe</c>)
/// when the container should actually present a desktop.
/// </param>
/// <param name="AutoLogonUser">User to log on automatically (null = do not touch auto-logon).</param>
/// <param name="AutoLogonPassword">That user's password (empty string for a blank one).</param>
public sealed record ContainerImageDesktopOptions(
    string? Shell = null,
    string? AutoLogonUser = null,
    string? AutoLogonPassword = null);

/// <summary>Result of <see cref="IContainerImageCustomizer.EnableWindowMirror"/>.</summary>
/// <param name="HiveFile">The <c>SOFTWARE</c> hive that was patched.</param>
/// <param name="UserinitBefore">The <c>Userinit</c> value before the patch.</param>
/// <param name="UserinitAfter">The <c>Userinit</c> value after the patch.</param>
/// <param name="PayloadFiles">Container-side paths of the installed payload files.</param>
public sealed record ContainerImageDesktopPrep(
    string HiveFile,
    string UserinitBefore,
    string UserinitAfter,
    IReadOnlyList<string> PayloadFiles,
    string? Shell = null,
    string? AutoLogonUser = null);

/// <summary>Result of <see cref="IContainerImageCustomizer.EnableInteractiveSession"/>.</summary>
/// <param name="HiveFile">The <c>SYSTEM</c> hive that was patched.</param>
/// <param name="ControlSets">The control sets the value was written to.</param>
public sealed record ContainerImageSessionPrep(string HiveFile, IReadOnlyList<string> ControlSets);

/// <summary>Optional settings for <see cref="IContainerImageCustomizer.EnableDesktopSession"/>.</summary>
/// <param name="UserName">The account auto-logon signs in as.</param>
/// <param name="Password">
/// That account's password. Must match what the engine sets with <c>net user</c> at
/// container start, or auto-logon fails and the session stays on LogonUI.
/// </param>
/// <param name="InstallWindowMirrorReadSide">
/// Also install the window-mirror payload (<c>wsm-desktop-read.ps1</c> and its launchers)
/// into <c>Files\wsm\</c>. The shell payload (<c>wsm-shell.cmd</c>) is always installed.
/// </param>
public sealed record DesktopSessionOptions(
    string UserName = ContainerDesktopSessionDefaults.DefaultUserName,
    string Password = ContainerDesktopSessionDefaults.DefaultPassword,
    bool InstallWindowMirrorReadSide = true);

/// <summary>Result of <see cref="IContainerImageCustomizer.EnableDesktopSession"/>.</summary>
/// <param name="Session">The console-session patch (which hive, which control sets).</param>
/// <param name="HiveFile">The <c>SOFTWARE</c> hive that received the logon settings.</param>
/// <param name="Shell">The shell <c>winlogon</c> starts after logon.</param>
/// <param name="UserName">The account auto-logon signs in as.</param>
/// <param name="PayloadFiles">Container-side paths of the installed payload files.</param>
public sealed record ContainerImageDesktopSessionPrep(
    ContainerImageSessionPrep Session,
    string HiveFile,
    string Shell,
    string UserName,
    IReadOnlyList<string> PayloadFiles);
