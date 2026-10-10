using Wsm.Connection.Session.Internal;
using Wsm.Domain.ValueObjects;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Covers <see cref="ConnectionRouter"/>: the RAIL-vs-full-desktop decision.
/// </summary>
public sealed class ConnectionRouterTests
{
    [Fact]
    public void RemoteApp_request_routes_to_the_direct_rail_path()
    {
        var runtime = new SandboxRuntimeInfo { RailMode = false };

        var path = ConnectionRouter.Route(SandboxConnectionMode.RemoteApp, runtime);

        Assert.Equal(ConnectionPath.RemoteApp, path);
    }

    [Fact]
    public void Full_desktop_request_on_a_rail_sandbox_routes_to_the_direct_path()
    {
        // The presentation is fixed at start time, so a RAIL sandbox stays RAIL.
        var runtime = new SandboxRuntimeInfo { RailMode = true };

        var path = ConnectionRouter.Route(SandboxConnectionMode.FullDesktop, runtime);

        Assert.Equal(ConnectionPath.RemoteApp, path);
    }

    [Fact]
    public void Full_desktop_request_on_a_desktop_sandbox_routes_to_the_packaged_client()
    {
        var runtime = new SandboxRuntimeInfo { RailMode = false };

        var path = ConnectionRouter.Route(SandboxConnectionMode.FullDesktop, runtime);

        Assert.Equal(ConnectionPath.FullDesktop, path);
    }
}
