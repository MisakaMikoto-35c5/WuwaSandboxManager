using System.Globalization;

namespace Wsm.Connection.ContainerDesktop.Internal;

internal enum ContainerDesktopCaptureHealth
{
    Unknown,
    Live,
    NoPixels,
    Error,
    Stopped,
}

internal readonly record struct ContainerDesktopCaptureStatus(
    int WriterPid,
    long Sequence,
    ContainerDesktopCaptureHealth Health);

internal static class MirrorCaptureStatus
{
    public const string FileSuffix = ".status";
    private const int CurrentVersion = 1;

    public static string PathFor(string mirrorPath) => mirrorPath + FileSuffix;

    public static ContainerDesktopCaptureStatus? TryRead(string mirrorPath)
    {
        try
        {
            var path = PathFor(mirrorPath);
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0
                    || !values.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim()))
                {
                    return null;
                }
            }

            if (values.Count != 4
                || !values.TryGetValue("version", out var version)
                || version != CurrentVersion.ToString(CultureInfo.InvariantCulture)
                || !values.TryGetValue("writerPid", out var pidText)
                || !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var writerPid)
                || writerPid <= 0
                || !values.TryGetValue("sequence", out var sequenceText)
                || !long.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
                || sequence < 0
                || !values.TryGetValue("health", out var health))
            {
                return null;
            }

            var parsedHealth = health switch
            {
                "live" => ContainerDesktopCaptureHealth.Live,
                "no-pixels" => ContainerDesktopCaptureHealth.NoPixels,
                "error" => ContainerDesktopCaptureHealth.Error,
                "stopped" => ContainerDesktopCaptureHealth.Stopped,
                "unknown" => ContainerDesktopCaptureHealth.Unknown,
                _ => (ContainerDesktopCaptureHealth?)null,
            };
            return parsedHealth is null
                ? null
                : new ContainerDesktopCaptureStatus(writerPid, sequence, parsedHealth.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
