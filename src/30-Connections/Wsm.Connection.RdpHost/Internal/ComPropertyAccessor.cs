using System.Diagnostics;
using System.Reflection;

namespace Wsm.Connection.RdpHost.Internal;

/// <summary>
/// Late-bound access to COM automation properties of the <c>mstscax</c> control.
/// The control is created through <see cref="System.Windows.Forms.AxHost"/>, so
/// its runtime type is <c>System.__ComObject</c>; named properties are reached
/// through <see cref="Type.InvokeMember(string, BindingFlags, Binder, object, object[])"/>,
/// which uses IDispatch.
/// <para>
/// <see cref="TrySet"/>/<see cref="TryInvoke"/> report success so the caller can
/// distinguish an optional property that is absent on a given control version
/// from a <i>required</i> one that failed; the latter must be surfaced, not
/// swallowed. <see cref="TryGetFirst"/> stays non-fatal because probing a
/// version-specific interface is inherently best-effort.
/// </para>
/// </summary>
internal static class ComPropertyAccessor
{
    /// <summary>Sets a property; returns <c>false</c> when the call failed.</summary>
    public static bool TrySet(object target, string property, object? value)
    {
        try
        {
            target.GetType().InvokeMember(
                property,
                BindingFlags.SetProperty,
                binder: null,
                target,
                new[] { value });
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Wsm.RdpHost] Unable to set COM property '{property}': {ex.Message}");
            return false;
        }
    }

    public static object? TryGetFirst(object target, params string[] properties)
    {
        foreach (var property in properties)
        {
            try
            {
                return target.GetType().InvokeMember(
                    property,
                    BindingFlags.GetProperty,
                    binder: null,
                    target,
                    args: null);
            }
            catch
            {
                // Property not present on this control version; try the next.
            }
        }

        return null;
    }

    /// <summary>Invokes a method; returns <c>false</c> when the call failed.</summary>
    public static bool TryInvoke(object target, string method, params object?[] args)
    {
        try
        {
            target.GetType().InvokeMember(
                method,
                BindingFlags.InvokeMethod,
                binder: null,
                target,
                args);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Wsm.RdpHost] Unable to invoke COM method '{method}': {ex.Message}");
            return false;
        }
    }
}
