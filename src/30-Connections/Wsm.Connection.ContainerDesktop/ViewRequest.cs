using System.Globalization;

namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// The experimental dynamic-resolution handshake between the frame viewer and the
/// in-container agent. The viewer writes <c>view-request.txt</c> (one line,
/// <c>&lt;width&gt;x&lt;height&gt;</c>) into the agent directory; the agent polls it,
/// tries <c>ChangeDisplaySettingsEx</c> on the session's display, and answers in
/// <c>view-request.ack.txt</c> (<c>applied WxH</c> or <c>rejected WxH (status N)</c>).
/// A viewer without an ack within <see cref="AckTimeout"/> keeps showing the frame
/// scaled instead - a failed change must never break the channel.
/// </summary>
public static class ViewRequest
{
    /// <summary>Name of the resolution request file the agent polls.</summary>
    public const string FileName = "view-request.txt";

    /// <summary>Name of the agent's answer file.</summary>
    public const string AckFileName = "view-request.ack.txt";

    /// <summary>How long a viewer waits for an ack before falling back to scaling.</summary>
    public static TimeSpan AckTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Writes a request: <c>1280x720</c>.</summary>
    public static string Format(int width, int height)
        => string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");

    /// <summary>Parses a request; only plausible desktop sizes are accepted.</summary>
    public static bool TryParse(string? text, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().ToLowerInvariant().Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height)
            && width is >= 200 and <= 8192
            && height is >= 200 and <= 8192;
    }

    /// <summary>Writes an ack: <c>applied 1280x720</c> or <c>rejected 1280x720</c>.</summary>
    public static string FormatAck(bool applied, int width, int height)
        => string.Create(CultureInfo.InvariantCulture, $"{(applied ? "applied" : "rejected")} {width}x{height}");

    /// <summary>
    /// Parses an ack; anything after the geometry (e.g. the agent's status detail) is ignored.
    /// </summary>
    public static bool TryParseAck(string? text, out bool applied, out int width, out int height)
    {
        applied = false;
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        if (string.Equals(parts[0], "applied", StringComparison.OrdinalIgnoreCase))
        {
            applied = true;
        }
        else if (!string.Equals(parts[0], "rejected", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return TryParse(parts[1], out width, out height);
    }
}
