namespace Wsm.Hcs.Storage;

/// <summary>
/// Best-effort release of the WCOW layers that keep an image's hive files locked.
/// <para>
/// Measured (docs/investigation/13-container-desktop-and-rdp.md section 25.8): a stopped
/// container leaves its <b>scratch layer activated</b>, and the scratch's WCIFS overlay
/// keeps the base image's hive files open — Restart Manager reports the locker as the
/// kernel (pid 4), <c>RegLoadKey</c> on the image's <c>SYSTEM</c>/<c>SOFTWARE</c> hive then
/// fails with <c>ERROR_SHARING_VIOLATION</c> (0x20), and neither restarting
/// <c>vmcompute</c> nor deactivating the image layer alone helps. Deactivating the stale
/// scratch layers as well releases the file.
/// </para>
/// <para>
/// Everything here is best effort: a layer that is genuinely in use by a running
/// container refuses the call, so only stale mounts are released.
/// </para>
/// </summary>
public static class WcowLayerRelease
{
    /// <summary>
    /// Unprepares/deactivates <paramref name="imageLayerPath"/> and every sandbox scratch
    /// layer under <paramref name="sandboxDataFolder"/> (which is what actually holds the
    /// image's files open). Returns one line per attempt for logging.
    /// </summary>
    public static IReadOnlyList<string> ReleaseImageLocks(
        IWcowLayerService layers, string imageLayerPath, string? sandboxDataFolder)
    {
        var log = new List<string>();

        Attempt(log, layers, imageLayerPath);

        if (!string.IsNullOrWhiteSpace(sandboxDataFolder) && Directory.Exists(sandboxDataFolder))
        {
            foreach (var sandbox in Directory.EnumerateDirectories(sandboxDataFolder))
            {
                IEnumerable<string> scratches;
                try
                {
                    scratches = Directory.EnumerateDirectories(sandbox, "scratch*");
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var scratch in scratches)
                {
                    Attempt(log, layers, scratch);
                }
            }
        }

        return log;
    }

    private static void Attempt(List<string> log, IWcowLayerService layers, string layerPath)
    {
        foreach (var operation in new[] { "unprepare", "deactivate" })
        {
            try
            {
                if (operation == "unprepare")
                {
                    layers.Unprepare(layerPath);
                }
                else
                {
                    layers.Deactivate(layerPath);
                }

                log.Add($"{operation} {layerPath}: ok");
            }
            catch (Exception ex)
            {
                // A layer that is not mounted, or is mounted by a running container,
                // refuses: both are fine, only the message is interesting.
                log.Add($"{operation} {layerPath}: {ex.Message}");
            }
        }
    }
}
