using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Service.Contracts;
using Wsm.Service.Contracts.Grpc.Generated;
using Wsm.Service.Contracts.Internal;
using Xunit;

namespace Wsm.Tests.Service;

/// <summary>
/// Tests for the container desktop parts of <see cref="WsmServiceMapping"/>:
/// request -&gt; desktop-plan override decisions and the session status DTO
/// mapping shared by the service host and its clients.
/// </summary>
public sealed class DesktopServiceMappingTests
{
    [Fact]
    public void ToDesktopPlan_without_overrides_keeps_the_base_plan_and_applies_present()
    {
        var basePlan = new SandboxDesktopPlan { Provider = "custom", PresentToInteractiveSession = true };
        var request = new StartDesktopRequest { Present = false };

        var plan = request.ToDesktopPlan(basePlan);

        Assert.Equal("custom", plan.Provider);
        Assert.Equal(basePlan.Kind, plan.Kind);
        Assert.False(plan.PresentToInteractiveSession);
    }

    [Fact]
    public void ToDesktopPlan_applies_the_kind_override()
    {
        var request = new StartDesktopRequest { Kind = (int)ContainerDesktopKind.WindowMirror };

        var plan = request.ToDesktopPlan(SandboxDesktopPlan.Default);

        Assert.Equal(ContainerDesktopKind.WindowMirror, plan.Kind);
    }

    [Fact]
    public void ToDesktopPlan_kind_zero_means_the_recipe_decides()
    {
        var basePlan = SandboxDesktopPlan.Default with { Kind = ContainerDesktopKind.None };
        var request = new StartDesktopRequest { Kind = 0 };

        var plan = request.ToDesktopPlan(basePlan);

        Assert.Equal(ContainerDesktopKind.None, plan.Kind);
    }

    [Fact]
    public void ToDesktopPlan_applies_the_provider_override()
    {
        var request = new StartDesktopRequest { Provider = "window-mirror" };

        var plan = request.ToDesktopPlan(SandboxDesktopPlan.Default);

        Assert.Equal("window-mirror", plan.Provider);
    }

    [Fact]
    public void ToDesktopPlan_rejects_an_undefined_kind()
    {
        var request = new StartDesktopRequest { Kind = 99 };

        Assert.Throws<SandboxValidationException>(() => request.ToDesktopPlan(SandboxDesktopPlan.Default));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, ContainerDesktopKind.FrameBuffer)]
    [InlineData(2, ContainerDesktopKind.WindowMirror)]
    public void ToContainerDesktopKindOverride_maps_wire_values(int wire, ContainerDesktopKind? expected)
        => Assert.Equal(expected, wire.ToContainerDesktopKindOverride());

    [Fact]
    public void ToContainerDesktopKindOverride_rejects_an_undefined_value()
        => Assert.Throws<SandboxValidationException>(() => 42.ToContainerDesktopKindOverride());

    [Fact]
    public void DesktopStatusReply_carries_the_live_channel_shape()
    {
        var reply = WsmServiceMapping.ToDesktopStatusReply(
            "window-mirror", ContainerDesktopKind.WindowMirror, @"D:\vm\frame.shm",
            1024, 768, 5, windowCount: 3, viewerProcessId: 4711);

        Assert.True(reply.Running);
        Assert.Equal("window-mirror", reply.ProviderId);
        Assert.Equal((int)ContainerDesktopKind.WindowMirror, reply.Kind);
        Assert.Equal(@"D:\vm\frame.shm", reply.FramePath);
        Assert.Equal(1024, reply.Width);
        Assert.Equal(768, reply.Height);
        Assert.Equal(5, reply.Fps);
        Assert.Equal(3, reply.WindowCount);
        Assert.Equal(4711, reply.ViewerProcessId);
    }

    [Fact]
    public void DesktopStatusReply_encodes_absent_values()
    {
        var reply = WsmServiceMapping.ToDesktopStatusReply(
            "frame-buffer", ContainerDesktopKind.FrameBuffer, @"D:\vm\frame.shm",
            1024, 768, 5, windowCount: null, viewerProcessId: null);

        Assert.Equal(-1, reply.WindowCount);
        Assert.Equal(0, reply.ViewerProcessId);
    }

    [Fact]
    public void ToContainerDesktopStatus_maps_a_running_reply()
    {
        var reply = WsmServiceMapping.ToDesktopStatusReply(
            "frame-buffer", ContainerDesktopKind.FrameBuffer, @"D:\vm\frame.shm",
            1024, 768, 5, windowCount: null, viewerProcessId: null);

        var status = reply.ToContainerDesktopStatus();

        Assert.True(status.Running);
        Assert.Equal("frame-buffer", status.ProviderId);
        Assert.Equal(ContainerDesktopKind.FrameBuffer, status.Kind);
        Assert.Equal(@"D:\vm\frame.shm", status.FramePath);
        Assert.Null(status.WindowCount);
        Assert.Null(status.ViewerProcessId);
    }

    [Fact]
    public void ToContainerDesktopStatus_maps_a_not_running_reply_to_the_shared_instance()
    {
        var status = WsmServiceMapping.ToNotRunningDesktopStatusReply().ToContainerDesktopStatus();

        Assert.False(status.Running);
        Assert.Same(ContainerDesktopStatus.NotRunning, status);
    }
}
