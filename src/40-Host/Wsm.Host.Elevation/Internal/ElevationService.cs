using System.Diagnostics;
using System.Security.Principal;
using Volo.Abp.DependencyInjection;

namespace Wsm.Host.Elevation.Internal;

/// <summary>
/// Default <see cref="IElevationService"/>: computes the elevation state locally
/// via <see cref="WindowsPrincipal"/> and relaunches the current executable with
/// the <c>runas</c> verb when a UAC prompt is required.
/// </summary>
[ExposeServices(typeof(IElevationService))]
internal sealed class ElevationService : IElevationService, ISingletonDependency
{
    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public bool TryRelaunchElevated(string? arguments = null)
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
