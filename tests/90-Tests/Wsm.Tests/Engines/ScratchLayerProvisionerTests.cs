using Wsm.Domain.Exceptions;
using Wsm.Engine.WindowsContainer;
using Wsm.Hcs.Storage;
using Xunit;

namespace Wsm.Tests.Engines;

/// <summary>
/// The scratch-layer provisioning policy. Measured (section 24 of the container investigation),
/// a scratch layer orphaned by a container whose process was killed can no longer be released -
/// <c>DeactivateLayer</c>/<c>DestroyLayer</c> and every later <c>CreateSandboxLayer</c> on that
/// path answer a sharing violation - so provisioning has to be able to move to a fresh path
/// instead of failing the start.
/// </summary>
public sealed class ScratchLayerProvisionerTests
{
    private static readonly IReadOnlyList<string> Image = [@"C:\images\servercore-ltsc2022"];

    [Fact]
    public void Provision_uses_the_requested_path_when_it_works()
    {
        var layers = new FakeLayers();

        var (path, mount) = ScratchLayerProvisioner.Provision(
            layers, @"C:\s\scratch", Image, out var attempts);

        Assert.Equal(@"C:\s\scratch", path);
        Assert.Single(attempts);
        Assert.Equal("ok", attempts[0].Reason);
        Assert.Equal(path, layers.Created[^1]);
        Assert.StartsWith(@"\\?\Volume", mount);
    }

    [Fact]
    public void Provision_moves_to_a_fresh_path_when_the_layer_is_orphaned()
    {
        var layers = new FakeLayers();
        layers.Orphaned.Add(@"C:\s\scratch");

        var (path, mount) = ScratchLayerProvisioner.Provision(
            layers, @"C:\s\scratch", Image, out var attempts);

        Assert.Equal(@"C:\s\scratch-2", path);
        Assert.Equal(2, attempts.Count);
        Assert.Equal(@"C:\s\scratch", attempts[0].Path);
        Assert.Null(attempts[0].MountPath);
        Assert.Contains("sharing violation", attempts[0].Reason);
        Assert.Equal(@"C:\s\scratch-2", attempts[1].Path);
        Assert.Equal(@"C:\s\scratch-2", layers.Created[^1]);
        Assert.StartsWith(@"\\?\Volume", mount);
    }

    [Fact]
    public void Provision_tries_every_fallback_and_reports_the_last_reason()
    {
        var layers = new FakeLayers { EverythingOrphaned = true };

        var ex = Assert.Throws<SandboxBackendException>(
            () => ScratchLayerProvisioner.Provision(layers, @"C:\s\scratch", Image, out _));

        Assert.Equal(ScratchLayerProvisioner.MaxAttempts, layers.Created.Count);
        Assert.Equal(@"C:\s\scratch-5", layers.Created[^1]);
        Assert.Contains(@"C:\s\scratch", ex.Message);
        Assert.Contains("sharing violation", ex.Message);
    }

    [Fact]
    public void Provision_treats_a_prepared_layer_without_a_mount_path_as_a_failure()
    {
        var layers = new FakeLayers { MountPathless = { @"C:\s\scratch" } };

        var (path, _) = ScratchLayerProvisioner.Provision(
            layers, @"C:\s\scratch", Image, out var attempts);

        Assert.Equal(@"C:\s\scratch-2", path);
        Assert.Equal("prepared but has no mount path", attempts[0].Reason);
    }

    [Theory]
    [InlineData(0, @"C:\s\scratch")]
    [InlineData(1, @"C:\s\scratch-2")]
    [InlineData(4, @"C:\s\scratch-5")]
    public void CandidatePath_numbers_the_fallbacks_from_two(int attempt, string expected)
    {
        Assert.Equal(expected, ScratchLayerProvisioner.CandidatePath(@"C:\s\scratch", attempt));
    }

    /// <summary>
    /// Stands in for the WCOW layer APIs. A path in <see cref="Orphaned"/> behaves the way the real
    /// driver does after a killed container: every call on it fails with a sharing violation.
    /// </summary>
    private sealed class FakeLayers : IWcowLayerService
    {
        public HashSet<string> Orphaned { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> MountPathless { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool EverythingOrphaned { get; set; }

        public List<string> Created { get; } = [];

        private readonly Dictionary<string, string> _mounts = new(StringComparer.OrdinalIgnoreCase);

        private bool IsOrphaned(string path) => EverythingOrphaned || Orphaned.Contains(path);

        public void CreateScratchLayer(string scratchPath, IReadOnlyList<string> parentLayerPaths)
        {
            Created.Add(scratchPath);
            if (IsOrphaned(scratchPath))
            {
                throw new IOException(
                    "The process cannot access the file because it is being used by another process. (sharing violation)");
            }

            _mounts[scratchPath] = MountPathless.Contains(scratchPath)
                ? string.Empty
                : "\\\\?\\Volume{" + Guid.NewGuid() + "}\\";
        }

        public void Activate(string layerPath)
        {
            if (IsOrphaned(layerPath))
            {
                throw new IOException("sharing violation");
            }
        }

        public void Prepare(string layerPath, IReadOnlyList<string> parentLayerPaths)
        {
            if (IsOrphaned(layerPath))
            {
                throw new IOException("sharing violation");
            }
        }

        public string GetMountPath(string layerPath)
            => _mounts.TryGetValue(layerPath, out var mount) ? mount : string.Empty;

        public void Unprepare(string layerPath)
        {
        }

        public void Deactivate(string layerPath)
        {
        }

        public void Destroy(string layerPath)
        {
        }

        public Guid GetLayerId(string layerPath) => Guid.Empty;

        public void ImportLayer(string layerPath, string importFolderPath, IReadOnlyList<string> parentLayerPaths)
            => throw new NotSupportedException();

        public void ProcessBaseImage(string layerPath) => throw new NotSupportedException();
    }
}
