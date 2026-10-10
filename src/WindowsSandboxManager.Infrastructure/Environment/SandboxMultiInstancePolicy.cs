using System.Security.Principal;
using Microsoft.Win32;

namespace WindowsSandboxManager.Infrastructure.Environment;

/// <summary>
/// Windows Sandbox's control plane gates concurrent instances behind the
/// <c>SandboxContracts.AreMultipleInstancesAllowed</c> policy, which is true only
/// on AVD/WVD hosts or internal ("rs_*") Windows branches. On retail client
/// builds it is false and the server returns <c>CO_E_APPSINGLEUSE</c> for the
/// second instance.
/// <para>
/// This helper exposes that policy and, only when explicitly requested by an
/// administrator, can flip the <c>BuildBranch</c> value so the host is treated
/// like an internal branch. This is a deliberate, reversible, machine-wide
/// change and is never applied automatically.
/// </para>
/// </summary>
public static class SandboxMultiInstancePolicy
{
    private const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string BuildBranchValue = "BuildBranch";
    private const string InternalBranch = "rs_prerelease";

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static string? CurrentBuildBranch
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(VersionKey);
            return key?.GetValue(BuildBranchValue) as string;
        }
    }

    /// <summary>
    /// Attempts to make the host report itself as an internal branch, which
    /// enables concurrent sandbox instances. Requires elevation.
    /// </summary>
    public static bool TryEnable(out string message)
    {
        if (!IsElevated)
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
    public static bool TryRestore(string? originalValue, out string message)
    {
        if (!IsElevated)
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
}
