using System.Linq;
using Wsm.Application.Internal;
using Xunit;

namespace Wsm.Tests.Application;

/// <summary>
/// Covers <see cref="GuestSessionTable.Parse"/>: the guest's <c>qwinsta</c> output
/// must yield exactly the real sessions (never the <c>services</c> session or the
/// RDP listener), which is what the connect-time reclaim inspects.
/// </summary>
public sealed class GuestSessionTableTests
{
    // The exact shape the WDAG guest produces (space-padded columns, blank
    // USERNAME, a ">services" marker and a named listener).
    private const string RealGuestOutput =
        " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
        ">services                                            0  Disc\n" +
        " 31c5ce94259d4006a9e4#0                              1  Conn\n" +
        " 31c5ce94259d4006a9e4                            65536  Listen\n";

    private static int[] Ids(string? output) =>
        GuestSessionTable.Parse(output).Select(session => session.Id).ToArray();

    [Fact]
    public void Empty_or_null_output_has_no_sessions()
    {
        Assert.Empty(GuestSessionTable.Parse(null));
        Assert.Empty(GuestSessionTable.Parse(string.Empty));
        Assert.Empty(GuestSessionTable.Parse("   \r\n"));
    }

    [Fact]
    public void Header_only_output_has_no_sessions()
    {
        var output = " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n";

        Assert.Empty(GuestSessionTable.Parse(output));
    }

    [Fact]
    public void Services_and_listener_are_not_sessions()
    {
        var output =
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n";

        Assert.Empty(GuestSessionTable.Parse(output));
    }

    [Fact]
    public void A_single_live_session_is_returned_as_connected()
    {
        var session = Assert.Single(GuestSessionTable.Parse(RealGuestOutput));

        Assert.Equal(1, session.Id);
        Assert.Equal("Conn", session.State);
        Assert.True(session.IsConnected);
    }

    [Fact]
    public void Disconnected_sessions_are_returned_in_order_and_are_orphans()
    {
        var output =
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            "                                                     3  Disc\n" +
            "                                                     1  Disc\n" +
            "                                                     2  Disc\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n";

        var sessions = GuestSessionTable.Parse(output);

        Assert.Equal([1, 2, 3], sessions.Select(session => session.Id).ToArray());
        Assert.All(sessions, session => Assert.False(session.IsConnected));
    }

    [Fact]
    public void A_named_user_session_is_returned_as_connected()
    {
        var output =
            " SESSIONNAME               USERNAME                 ID  STATE   TYPE        DEVICE\n" +
            ">services                                            0  Disc\n" +
            " 31c5ce94259d4006a9e4#0  WDAGUtilityAccount           1  Active\n" +
            " 31c5ce94259d4006a9e4                            65536  Listen\n";

        var session = Assert.Single(GuestSessionTable.Parse(output));

        Assert.Equal(1, session.Id);
        Assert.True(session.IsConnected);
    }

    [Fact]
    public void Real_guest_output_yields_only_the_live_session()
    {
        Assert.Equal([1], Ids(RealGuestOutput));
    }
}
