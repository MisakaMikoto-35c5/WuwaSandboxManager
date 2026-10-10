using Wsm.Domain;
using Wsm.Domain.ValueObjects;
using Wsm.Persistence;
using Xunit;

namespace Wsm.Tests.Persistence;

/// <summary>Covers the <c>.wsbx</c> sandbox manifest JSON format round-trip.</summary>
public sealed class SandboxManifestTests
{
    [Fact]
    public void Manifest_round_trips_through_json()
    {
        var id = Guid.NewGuid();
        var recipe = new SandboxRecipe
        {
            Name = new SandboxName("Ribbon"),
            Memory = new SandboxMemoryConfiguration(8192),
            Cpu = new SandboxCpuConfiguration(6),
            RailMode = true,
            FullScreen = true,
            MappedFolders = new[] { new MappedFolder(@"C:\src", @"C:\share", readOnly: false) },
            LogonCommands = new[] { new LogonCommand("cmd.exe /c echo hi") },
        };

        var manifest = new SandboxManifest
        {
            Id = id,
            Name = "Ribbon",
            RailMode = true,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
            Recipe = recipe,
        };

        var parsed = SandboxManifest.FromJson(manifest.ToJson());

        Assert.NotNull(parsed);
        Assert.Equal(SandboxManifest.CurrentSchema, parsed!.Schema);
        Assert.Equal(id, parsed.Id);
        Assert.Equal("Ribbon", parsed.Name);
        Assert.True(parsed.RailMode);
        Assert.Equal("RemoteApp", parsed.Type);
        Assert.Equal(8192, parsed.Recipe.Memory.MemoryMb);
        Assert.Equal(6, parsed.Recipe.Cpu.CoreCount);
        Assert.True(parsed.Recipe.FullScreen);
        var folder = Assert.Single(parsed.Recipe.MappedFolders);
        Assert.Equal(@"C:\src", folder.HostFolder);
        Assert.False(folder.ReadOnly);
        var command = Assert.Single(parsed.Recipe.LogonCommands);
        Assert.Equal("cmd.exe /c echo hi", command.Command);
    }

    [Fact]
    public void Manifest_writes_and_reads_a_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), "wsm-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():D}.wsbx");

        try
        {
            var manifest = SandboxManifest.FromInstance(SandboxInstance.Create(new SandboxName("File")));
            File.WriteAllText(path, manifest.ToJson());

            var read = SandboxManifest.TryReadFile(path);

            Assert.NotNull(read);
            Assert.Equal(manifest.Id, read!.Id);
            Assert.Equal("File", read.Name);
            Assert.False(read.RailMode);
            Assert.Equal("FullDesktop", read.Type);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryReadFile_returns_null_for_a_missing_file()
    {
        Assert.Null(SandboxManifest.TryReadFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("D") + ".wsbx")));
    }
}
