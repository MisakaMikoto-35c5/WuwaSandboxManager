using Wsm.Domain;
using Wsm.Domain.Events;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Xunit;

namespace Wsm.Tests.Domain;

public class DomainTests
{
    [Fact]
    public void Recipe_defaults_match_windows_sandbox_defaults()
    {
        var recipe = SandboxRecipe.Default;

        Assert.Equal(4096, recipe.Memory.MemoryMb);
        Assert.Equal(NetworkingMode.Default, recipe.Networking);
        Assert.Equal(VirtualGpuMode.Default, recipe.VirtualGpu);
        Assert.Equal(RdpTransportKind.NamedPipe, recipe.Transport);
        Assert.True(recipe.Redirections.Clipboard);
        Assert.False(recipe.Redirections.VideoInput);
        Assert.False(recipe.RailMode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(1024)]
    public void Memory_below_the_sandbox_floor_is_rejected(int memoryMb)
        => Assert.Throws<SandboxValidationException>(() => new SandboxMemoryConfiguration(memoryMb));

    [Fact]
    public void Memory_at_the_floor_is_accepted()
        => Assert.Equal(2048, new SandboxMemoryConfiguration(2048).MemoryMb);

    [Fact]
    public void Instance_lifecycle_transitions_and_raises_events()
    {
        var instance = SandboxInstance.Create(new SandboxName("Test"));
        Assert.Equal(SandboxState.Created, instance.State);
        Assert.Contains(instance.DomainEvents, e => e is SandboxInstanceCreatedEvent);

        instance.BeginStart();
        Assert.Equal(SandboxState.Starting, instance.State);

        var runtime = new SandboxRuntimeInfo
        {
            SandboxId = instance.Id,
            VmId = Guid.NewGuid(),
            Transport = RdpTransportKind.NamedPipe,
        };
        instance.MarkRunning(runtime);

        Assert.Equal(SandboxState.Running, instance.State);
        Assert.Equal(runtime, instance.Runtime);
        Assert.Contains(instance.DomainEvents, e => e is SandboxStartedEvent);

        instance.MarkStopped();
        Assert.Equal(SandboxState.Stopped, instance.State);
        Assert.Null(instance.Runtime);
    }

    [Fact]
    public void Configuration_cannot_change_while_running()
    {
        var instance = SandboxInstance.Create();
        instance.BeginStart();
        instance.MarkRunning(new SandboxRuntimeInfo { SandboxId = instance.Id, VmId = Guid.NewGuid() });

        Assert.Throws<SandboxDomainException>(() =>
            instance.ChangeConfiguration(SandboxRecipe.Default with { Memory = new SandboxMemoryConfiguration(8192) }));
    }

    [Fact]
    public void ForConnectionMode_sets_rail_flag()
    {
        var recipe = SandboxRecipe.Default;
        Assert.True(recipe.ForConnectionMode(SandboxConnectionMode.RemoteApp).RailMode);
        Assert.False(recipe.ForConnectionMode(SandboxConnectionMode.FullDesktop).RailMode);
    }

    [Fact]
    public void Runtime_named_pipe_endpoint_is_derived_from_vm_id()
    {
        var vmId = Guid.Parse("aa4f54a6-d304-4893-88b6-cb947a7795e6");
        var runtime = new SandboxRuntimeInfo { VmId = vmId };
        Assert.Equal(@"\\.\pipe\aa4f54a6-d304-4893-88b6-cb947a7795e6", runtime.NamedPipeEndpoint);
    }
}
