using Microsoft.Extensions.DependencyInjection;
using Volo.Abp;
using Volo.Abp.Modularity;
using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Recipe.Serialization;
using Xunit;

namespace Wsm.Tests.Recipe;

/// <summary>
/// Test-only startup module that boots the .wsb recipe serialization module so
/// the tests can exercise the module-resolved <see cref="ISandboxRecipeSerializer"/>
/// contract rather than constructing the internal implementation directly.
/// </summary>
[DependsOn(typeof(WsbRecipeSerializationModule))]
public sealed class RecipeTestModule : AbpModule
{
}

public class WsbRecipeSerializerTests
{
    private static async Task WithSerializerAsync(Func<ISandboxRecipeSerializer, Task> test)
    {
        using var app = await AbpApplicationFactory.CreateAsync<RecipeTestModule>();
        await app.InitializeAsync();

        try
        {
            await test(app.ServiceProvider.GetRequiredService<ISandboxRecipeSerializer>());
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Fact]
    public Task Serialize_emits_the_expected_wsb_shape() => WithSerializerAsync(serializer =>
    {
        var recipe = SandboxRecipe.Default with
        {
            Name = new SandboxName("My Sandbox"),
            Memory = new SandboxMemoryConfiguration(8192),
            VirtualGpu = VirtualGpuMode.Disabled,
            Networking = NetworkingMode.Enabled,
            RailMode = true,
            MappedFolders = new[] { new MappedFolder(@"C:\data", @"C:\share", readOnly: true) },
            LogonCommands = new[] { new LogonCommand("cmd.exe /c echo hi") },
        };

        var xml = serializer.Serialize(recipe);

        Assert.StartsWith("<Configuration>", xml);
        Assert.Contains("<DisplayName>My Sandbox</DisplayName>", xml);
        Assert.Contains("<MemoryInMB>8192</MemoryInMB>", xml);
        Assert.Contains("<vGPU>Disable</vGPU>", xml);
        Assert.Contains("<Networking>Enable</Networking>", xml);
        Assert.Contains("<RailMode>Enable</RailMode>", xml);
        Assert.Contains("<HostFolder>C:\\data</HostFolder>", xml);
        Assert.Contains("<ReadOnly>true</ReadOnly>", xml);
        Assert.Contains("<Command>cmd.exe /c echo hi</Command>", xml);

        return Task.CompletedTask;
    });

    [Fact]
    public Task Tri_state_default_flags_are_omitted() => WithSerializerAsync(serializer =>
    {
        var xml = serializer.Serialize(SandboxRecipe.Default);
        Assert.DoesNotContain("<vGPU>", xml);
        Assert.DoesNotContain("<Networking>", xml);

        return Task.CompletedTask;
    });

    [Fact]
    public Task Round_trip_preserves_the_configuration() => WithSerializerAsync(serializer =>
    {
        var original = SandboxRecipe.Default with
        {
            Name = new SandboxName("Round Trip"),
            Memory = new SandboxMemoryConfiguration(6144),
            VirtualGpu = VirtualGpuMode.Enabled,
            Networking = NetworkingMode.Disabled,
            Transport = RdpTransportKind.Tcp,
            FullScreen = true,
            Redirections = new SandboxRedirections { Clipboard = false, Printer = true },
            MappedFolders = new[] { new MappedFolder(@"C:\src", @"C:\dst", readOnly: false) },
        };

        var restored = serializer.Deserialize(serializer.Serialize(original));

        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Memory, restored.Memory);
        Assert.Equal(original.VirtualGpu, restored.VirtualGpu);
        Assert.Equal(original.Networking, restored.Networking);
        Assert.Equal(original.Transport, restored.Transport);
        Assert.True(restored.FullScreen);
        Assert.False(restored.Redirections.Clipboard);
        Assert.True(restored.Redirections.Printer);
        Assert.Single(restored.MappedFolders);
        Assert.False(restored.MappedFolders[0].ReadOnly);

        return Task.CompletedTask;
    });

    [Fact]
    public Task Deserialize_accepts_legacy_true_false_readonly() => WithSerializerAsync(serializer =>
    {
        var xml = "<Configuration><MappedFolders><MappedFolder><HostFolder>C:\\x</HostFolder><ReadOnly>true</ReadOnly></MappedFolder></MappedFolders></Configuration>";
        var recipe = serializer.Deserialize(xml);
        Assert.True(recipe.MappedFolders[0].ReadOnly);

        return Task.CompletedTask;
    });
}
