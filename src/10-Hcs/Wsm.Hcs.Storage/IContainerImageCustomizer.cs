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
}

/// <summary>Result of <see cref="IContainerImageCustomizer.EnableInteractiveSession"/>.</summary>
/// <param name="HiveFile">The <c>SYSTEM</c> hive that was patched.</param>
/// <param name="ControlSets">The control sets the value was written to.</param>
public sealed record ContainerImageSessionPrep(string HiveFile, IReadOnlyList<string> ControlSets);
