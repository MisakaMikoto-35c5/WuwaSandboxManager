using System.Globalization;

namespace Wsm.Rdp.WinUI;

/// <summary>Command line parsed by the connector host.</summary>
internal sealed record ConnectorArguments(string RequestPath, int? ParentProcessId)
{
    /// <summary>
    /// Parses <c>--request &lt;path&gt; [--parent-pid &lt;pid&gt;]</c>. Returns null when no
    /// request path is present (the process then exits).
    /// </summary>
    public static ConnectorArguments? Parse(string[] args)
    {
        string? requestPath = null;
        int? parentProcessId = null;

        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--request" when i + 1 < args.Length:
                    requestPath = args[++i];
                    break;
                case "--parent-pid" when i + 1 < args.Length &&
                                         int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid):
                    parentProcessId = pid;
                    break;
            }
        }

        return string.IsNullOrWhiteSpace(requestPath)
            ? null
            : new ConnectorArguments(requestPath, parentProcessId);
    }
}
