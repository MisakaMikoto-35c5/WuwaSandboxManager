using Microsoft.Win32;
using Volo.Abp.DependencyInjection;
using Wsm.Host.Elevation;

namespace Wsm.Host.Policy.Internal;

/// <summary>
/// Default <see cref="IMultiInstancePolicy"/>: exposes the host's multi-instance
/// policy and can flip the <c>BuildBranch</c> value so the host is treated like
/// an internal branch. This is a deliberate, reversible, machine-wide change and
/// is never applied automatically.
/// </summary>
[ExposeServices(typeof(IMultiInstancePolicy))]
internal sealed class MultiInstancePolicy : IMultiInstancePolicy, ISingletonDependency
{
    private const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string BuildBranchValue = "BuildBranch";
    private const string InternalBranch = "rs_prerelease";
    private const string WvdEnabledValueName = "TerminalServices-RemoteConnectionManager-WVD-Enabled";

    private readonly IElevationService _elevation;

    public MultiInstancePolicy(IElevationService elevation)
    {
        _elevation = elevation;
    }

    public string? CurrentBuildBranch
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(VersionKey);
            return key?.GetValue(BuildBranchValue) as string;
        }
    }

    public bool AllowsMultipleInstances => IsWvdEnabled() || IsInternalBranch();

    /// <summary>
    /// Attempts to make the host report itself as an internal branch, which
    /// enables concurrent sandbox instances. Requires elevation.
    /// </summary>
    public bool TryEnable(out string message)
    {
        if (!_elevation.IsElevated)
        {
            message = "Administrator privileges are required to change the multi-instance policy.";
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(VersionKey, writable: true)
                            ?? throw new InvalidOperationException("Unable to open the Windows version key.");
            key.SetValue(BuildBranchValue, InternalBranch, RegistryValueKind.String);
            message = $"Multi-instance policy enabled (BuildBranch set to '{InternalBranch}'). " +
                      "Restart the Windows Sandbox server (kill WindowsSandboxServer.exe) for it to take effect.";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Failed to enable the multi-instance policy: {ex.Message}";
            return false;
        }
    }

    /// <summary>Restores the original <c>BuildBranch</c> value.</summary>
    public bool TryRestore(string? originalValue, out string message)
    {
        if (!_elevation.IsElevated)
        {
            message = "Administrator privileges are required to change the multi-instance policy.";
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(VersionKey, writable: true)
                            ?? throw new InvalidOperationException("Unable to open the Windows version key.");

            if (originalValue is null)
            {
                key.DeleteValue(BuildBranchValue, throwOnMissingValue: false);
            }
            else
            {
                key.SetValue(BuildBranchValue, originalValue, RegistryValueKind.String);
            }

            message = $"Multi-instance policy restored (BuildBranch = '{originalValue ?? "<none>"}').";
            return true;
        }
        catch (Exception ex)
        {
            message = $"Failed to restore the multi-instance policy: {ex.Message}";
            return false;
        }
    }

    private static bool IsWvdEnabled()
    {
        try
        {
            var value = SlGetWindowsInformationDword(WvdEnabledValueName, out var data);
            return value == 0 && data != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInternalBranch()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(VersionKey);
            var branch = key?.GetValue(BuildBranchValue) as string ?? string.Empty;
            // ^(rs(?!.*release).+)  -> internal pre-release branches.
            if (!branch.StartsWith("rs", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !branch.Contains("release", StringComparison.OrdinalIgnoreCase) && branch.Length > 2;
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("slc.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SLGetWindowsInformationDWORD(string valueName, out uint value);

    private static int SlGetWindowsInformationDword(string valueName, out uint value)
        => SLGetWindowsInformationDWORD(valueName, out value);
}
