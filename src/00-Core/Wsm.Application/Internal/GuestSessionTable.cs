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
/// reports a header line followed by one line per session; the entries that
/// are not real logon sessions are the <c>services</c> session (id 0) and any
/// winstation in state <c>Listen</c>: the container hvsocket listener (id
/// 65536) and, once TCP RDP is enabled in the guest, the RDP-Tcp listener
/// (id 65537). Everything else is a session a previous connection may have
/// left behind.
/// </summary>
internal static class GuestSessionTable
{
    /// <summary>Session id of the special <c>services</c> session.</summary>
    internal const int ServicesSessionId = 0;

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

            if (id == ServicesSessionId)
            {
                continue;
            }

            var state = tokens[^1];

            // Any winstation in Listen state is a listener, never a session to
            // reclaim. Filtering by state (not by the listener's id) matters:
            // the hvsocket listener is 65536, but the RDP-Tcp listener takes
            // 65537, and logging off a listener is impossible — the reclaim
            // loop would wait it out for the full timeout.
            if (state.Equals("Listen", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!sessions.Any(session => session.Id == id))
            {
                sessions.Add(new GuestSession(id, state));
            }
        }

        sessions.Sort((left, right) => left.Id.CompareTo(right.Id));
        return sessions;
    }
}
