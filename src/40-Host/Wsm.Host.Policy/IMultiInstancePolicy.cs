namespace Wsm.Host.Policy;

/// <summary>
/// Windows Sandbox's control plane gates concurrent instances behind the
/// <c>SandboxContracts.AreMultipleInstancesAllowed</c> policy, which is true only
/// on AVD/WVD hosts or internal ("rs_*") Windows branches.
/// </summary>
public interface IMultiInstancePolicy
{
    /// <summary>The current <c>BuildBranch</c> value read from the registry, if any.</summary>
    string? CurrentBuildBranch { get; }

    /// <summary>
    /// True when the host allows more than one sandbox at a time (WDAG/AVD hosts
    /// or internal builds).
    /// </summary>
    bool AllowsMultipleInstances { get; }

    /// <summary>
    /// Attempts to make the host report itself as an internal branch, which
    /// enables concurrent sandbox instances. Requires elevation.
    /// </summary>
    bool TryEnable(out string message);

    /// <summary>Restores the original <c>BuildBranch</c> value.</summary>
    bool TryRestore(string? originalValue, out string message);
}
