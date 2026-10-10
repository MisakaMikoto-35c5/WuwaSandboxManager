using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.Legacy.Internal;

/// <summary>
/// Manages the per-instance transient files the legacy path drops in
/// <c>%TEMP%\WindowsSandboxManager</c> (today only the <c>.wsb</c> recipe; the
/// obsolete <c>.rdp</c> files are swept too). Without this the files accumulate
/// forever. A stale-age sweep runs before every write, so the directory never
/// grows without bound across launches.
/// </summary>
internal static class TemporarySandboxFiles
{
    /// <summary>Directory holding transient recipe/config files.</summary>
    public static string DirectoryPath { get; } =
        Path.Combine(Path.GetTempPath(), "WindowsSandboxManager");

    /// <summary>Files older than this are removed by the next launch.</summary>
    public static TimeSpan StaleAge { get; } = TimeSpan.FromHours(6);

    /// <summary>Extension used by the legacy recipe file.</summary>
    public const string RecipeExtension = ".wsb";

    /// <summary>
    /// Prepares <paramref name="directory"/> for a new write: ensures it exists
    /// and sweeps transient files left behind by earlier launches.
    /// </summary>
    public static void Prepare(string directory)
    {
        System.IO.Directory.CreateDirectory(directory);
        Sweep(directory, StaleAge);
    }

    /// <summary>Returns the per-instance path for <paramref name="id"/>.</summary>
    public static string PathFor(string directory, SandboxInstanceId id, string extension)
        => Path.Combine(directory, $"{id.Value:D}{extension}");

    /// <summary>
    /// Deletes transient files (<c>.wsb</c>/<c>.rdp</c>) in
    /// <paramref name="directory"/> whose last write is older than
    /// <paramref name="maxAge"/>. Best effort: any individual failure is ignored.
    /// </summary>
    public static void Sweep(string directory, TimeSpan maxAge)
    {
        try
        {
            if (!System.IO.Directory.Exists(directory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var file in System.IO.Directory.EnumerateFiles(directory))
            {
                if (!IsTransient(Path.GetExtension(file)))
                {
                    continue;
                }

                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Locked or already gone; the next launch retries.
                }
            }
        }
        catch
        {
            // Directory disappeared or is inaccessible; nothing to clean.
        }
    }

    private static bool IsTransient(string extension)
        => extension.Equals(RecipeExtension, StringComparison.OrdinalIgnoreCase)
           || extension.Equals(".rdp", StringComparison.OrdinalIgnoreCase);
}
