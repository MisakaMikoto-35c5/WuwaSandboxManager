namespace Wsm.Host.Elevation;

/// <summary>
/// Detects whether the current process is elevated and, when it is not, can
/// relaunch it with a UAC prompt. HCS (and therefore the HCS sandbox engine)
/// requires administrator / Hyper-V Administrators membership.
/// </summary>
public interface IElevationService
{
    /// <summary>True when the current process can call HCS (administrator).</summary>
    bool IsElevated { get; }

    /// <summary>Relaunches the current executable elevated, returning false if declined.</summary>
    bool TryRelaunchElevated(string? arguments = null);
}
