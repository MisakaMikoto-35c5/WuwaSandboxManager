namespace Wsm.Application;

/// <summary>
/// Pure helpers for interpreting the process command line. The WinUI host uses
/// this to implement "open with" for <c>.wsbx</c> sandbox manifests.
/// </summary>
public static class WsmLaunchArguments
{
    /// <summary>The manifest file extension understood by the app.</summary>
    public const string ManifestExtension = ".wsbx";

    /// <summary>
    /// Returns the first argument that is a <c>.wsbx</c> manifest path, or
    /// <c>null</c> when none is present. Never throws on unknown arguments.
    /// </summary>
    public static string? GetManifestPath(IEnumerable<string>? args)
    {
        if (args is null)
        {
            return null;
        }

        foreach (var arg in args)
        {
            if (IsManifestPath(arg))
            {
                return arg.Trim().Trim('"');
            }
        }

        return null;
    }

    /// <summary>True when the argument looks like a <c>.wsbx</c> manifest path (not a switch).</summary>
    public static bool IsManifestPath(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return false;
        }

        var trimmed = arg.Trim().Trim('"');
        return !trimmed.StartsWith("-", StringComparison.Ordinal)
            && trimmed.EndsWith(ManifestExtension, StringComparison.OrdinalIgnoreCase);
    }
}
