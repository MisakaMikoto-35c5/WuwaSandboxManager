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
    ContainerImageDesktopPrep EnableWindowMirror(string layerPath);
}

/// <summary>Result of <see cref="IContainerImageCustomizer.EnableWindowMirror"/>.</summary>
/// <param name="HiveFile">The <c>SOFTWARE</c> hive that was patched.</param>
/// <param name="UserinitBefore">The <c>Userinit</c> value before the patch.</param>
/// <param name="UserinitAfter">The <c>Userinit</c> value after the patch.</param>
/// <param name="PayloadFiles">Container-side paths of the installed payload files.</param>
public sealed record ContainerImageDesktopPrep(
    string HiveFile,
    string UserinitBefore,
    string UserinitAfter,
    IReadOnlyList<string> PayloadFiles);

/// <summary>Result of <see cref="IContainerImageCustomizer.EnableInteractiveSession"/>.</summary>
/// <param name="HiveFile">The <c>SYSTEM</c> hive that was patched.</param>
/// <param name="ControlSets">The control sets the value was written to.</param>
public sealed record ContainerImageSessionPrep(string HiveFile, IReadOnlyList<string> ControlSets);
