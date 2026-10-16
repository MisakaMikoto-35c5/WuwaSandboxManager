using Wsm.Domain.Exceptions;
using Wsm.Hcs.Storage;

namespace Wsm.Engine.WindowsContainer;

/// <summary>
/// The scratch-layer provisioning policy for a process-isolated container: create the layer,
/// mount it, union the read-only image beneath it - and, when a path cannot be provisioned, step
/// to a fresh one.
/// <para>
/// Measured (docs/investigation/13-container-desktop-and-rdp.md section 24): a scratch layer whose
/// VHDX is still attached to a compute system that has gone (a container whose process was killed
/// instead of stopped) is <b>orphaned</b>. On that path <c>DeactivateLayer</c> and
/// <c>DestroyLayer</c> answer <c>0x80070020</c> (sharing violation) and every later
/// <c>CreateSandboxLayer</c>/<c>ActivateLayer</c> fails the same way, so no call can recover it
/// for the rest of the boot. What does work is a different directory: the layer id HCS uses is a
/// hash of the path, so <c>&lt;scratch&gt;-2</c> provisions cleanly and the container simply uses
/// whichever volume came up.
/// </para>
/// </summary>
internal static class ScratchLayerProvisioner
{
    /// <summary>How many paths (the requested one plus fallbacks) are tried before giving up.</summary>
    internal const int MaxAttempts = 5;

    /// <summary>One provisioning attempt, for the caller's log.</summary>
    internal sealed record Attempt(string Path, string? MountPath, string Reason);

    /// <summary>Path suffix used by the <paramref name="attempt"/>-th fallback (2-based, as in "-2").</summary>
    internal static string CandidatePath(string scratchPath, int attempt)
        => attempt == 0 ? scratchPath : $"{scratchPath}-{attempt + 1}";

    /// <summary>
    /// Provisions a scratch layer and returns the path that was used together with its mount path.
    /// <paramref name="attempts"/> receives every attempt in order, so the caller can report why
    /// the fallback was needed.
    /// </summary>
    internal static (string Path, string MountPath) Provision(
        IWcowLayerService layers,
        string scratchPath,
        IReadOnlyList<string> baseLayers,
        out IReadOnlyList<Attempt> attempts)
    {
        var log = new List<Attempt>(MaxAttempts);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var candidate = CandidatePath(scratchPath, attempt);
            try
            {
                Directory.CreateDirectory(candidate);
                layers.CreateScratchLayer(candidate, baseLayers);
                layers.Activate(candidate);
                layers.Prepare(candidate, baseLayers);
                var mount = layers.GetMountPath(candidate);
                if (!string.IsNullOrWhiteSpace(mount))
                {
                    log.Add(new Attempt(candidate, mount, "ok"));
                    attempts = log;
                    return (candidate, mount);
                }

                log.Add(new Attempt(candidate, null, "prepared but has no mount path"));
            }
            catch (Exception ex)
            {
                log.Add(new Attempt(candidate, null, ex.Message));
            }
        }

        attempts = log;
        throw new SandboxBackendException(
            $"No scratch layer could be provisioned for '{scratchPath}' after {MaxAttempts} attempts "
            + $"(last: {log[^1].Reason}).",
            0);
    }
}
