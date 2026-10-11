namespace Wsm.Application.Internal;

/// <summary>
/// A guest Remote Desktop Services session as reported by <c>qwinsta</c>.
/// </summary>
/// <param name="Id">The session id.</param>
/// <param name="State">The session state token, e.g. <c>Conn</c>, <c>Disc</c>, <c>Listen</c>.</param>
internal sealed record GuestSession(int Id, string State)
{
    /// <summary>
    /// True when the session is still attached to a live client. A live session
    /// is the caller's own (or a concurrent connect) and must be left alone; only
    /// an orphaned, disconnected session needs reclaiming.
    /// </summary>
    internal bool IsConnected =>
        State.Equals("Conn", StringComparison.OrdinalIgnoreCase) ||
        State.Equals("Connected", StringComparison.OrdinalIgnoreCase) ||
        State.Equals("Active", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Parses the guest's <c>qwinsta</c> (QUERY SESSION) output. The WDAG guest
/// reports a header line followed by one line per session; two of those entries
/// are not real logon sessions: the <c>services</c> session (id 0) and the RDP
/// listener (state <c>Listen</c>, id 65536). Everything else is a session a
/// previous connection may have left behind.
/// </summary>
internal static class GuestSessionTable
{
    /// <summary>Session id of the special <c>services</c> session.</summary>
    internal const int ServicesSessionId = 0;

    /// <summary>Session id the guest reports for an RDP listener.</summary>
    internal const int ListenerSessionId = 65536;

    /// <summary>
    /// Returns the real (non-services, non-listener) sessions in
    /// <paramref name="qwinstaOutput"/>, ascending by id and de-duplicated.
    /// <para>
    /// The parse is deliberately tolerant: the columns are space padded and
    /// <c>USERNAME</c> is frequently blank, so the session id is read as the
    /// token before the trailing state column. Any line whose id column is not an
    /// integer (the header, blank lines, continuation text) is ignored.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<GuestSession> Parse(string? qwinstaOutput)
    {
        if (string.IsNullOrWhiteSpace(qwinstaOutput))
        {
            return [];
        }

        var sessions = new List<GuestSession>();
        foreach (var line in qwinstaOutput.Split('\n'))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            if (!int.TryParse(tokens[^2], out var id))
            {
                continue;
            }

            if (id == ServicesSessionId || id == ListenerSessionId)
            {
                continue;
            }

            var state = tokens[^1];
            if (!sessions.Any(session => session.Id == id))
            {
                sessions.Add(new GuestSession(id, state));
            }
        }

        sessions.Sort((left, right) => left.Id.CompareTo(right.Id));
        return sessions;
    }
}
