using System.Diagnostics;

namespace WindowsSandboxManager.Infrastructure.Elevation;

/// <summary>
/// Detects whether the current process is elevated and, when it is not, can
/// relaunch it with a UAC prompt. HCS (and therefore the HCS sandbox backend)
/// requires administrator / Hyper-V Administrators membership.
/// </summary>
public static class ElevationHelper
{
    public static bool IsElevated => Hcs.HcsHostLayout.IsElevated;

    /// <summary>Relaunches the current executable elevated, returning false if declined.</summary>
    public static bool TryRelaunchElevated(string? arguments = null)
    {
        try
        {
            var path = System.Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var info = new ProcessStartInfo
            {
                FileName = path,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
                Verb = "runas",
            };

            Process.Start(info);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
