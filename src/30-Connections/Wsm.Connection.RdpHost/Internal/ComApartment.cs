using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Wsm.Connection.RdpHost.Internal;

/// <summary>
/// COM apartment helpers for the RDPBASE endpoint.
/// <para>
/// The endpoint RCW is produced on the RDPBASE callback thread and then handed
/// to the WinForms STA hosting thread. If the object is not apartment-agile, COM
/// must marshal it across apartments; if it cannot, the control will fail to
/// connect. Rather than fail silently, <see cref="WarnIfNotAgile"/> probes the
/// object's agility and logs the outcome.
/// </para>
/// </summary>
internal static class ComApartment
{
    // IID_IAgileObject {94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90}. An object that
    // implements it can be used from any apartment without marshalling.
    private static readonly Guid IAgileObject = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");

    /// <summary>
    /// Returns <c>true</c> when <paramref name="instance"/> advertises
    /// <c>IAgileObject</c> (i.e. is safe to use across apartments).
    /// </summary>
    public static bool IsAgile(object instance)
    {
        var unknown = nint.Zero;
        var agile = nint.Zero;
        try
        {
            unknown = Marshal.GetIUnknownForObject(instance);
            if (unknown == nint.Zero)
            {
                return false;
            }

            var iid = IAgileObject;
            var hr = Marshal.QueryInterface(unknown, in iid, out agile);
            return hr == 0 && agile != nint.Zero;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (agile != nint.Zero)
            {
                Marshal.Release(agile);
            }

            if (unknown != nint.Zero)
            {
                Marshal.Release(unknown);
            }
        }
    }

    /// <summary>
    /// Logs a warning when <paramref name="endpoint"/> is not apartment-agile, so
    /// a cross-apartment failure is diagnosed instead of being mistaken for a
    /// transport problem.
    /// </summary>
    public static void WarnIfNotAgile(object endpoint, ILogger logger)
    {
        if (IsAgile(endpoint))
        {
            logger.LogDebug("The RDPBASE endpoint is apartment-agile; it is safe to use on the hosting STA thread.");
            return;
        }

        logger.LogWarning(
            "The RDPBASE endpoint is not marked IAgileObject and is being used on a different apartment than the " +
            "thread that created it. If the session fails to connect, acquire the endpoint on the hosting STA thread.");
    }
}
