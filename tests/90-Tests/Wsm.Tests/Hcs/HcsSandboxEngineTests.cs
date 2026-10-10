using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engine.Hcs;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Checkpoint;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Sandbox;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Storage;
using Wsm.Host.Elevation;
using Xunit;

namespace Wsm.Tests.Hcs;

/// <summary>
/// Unit tests for <see cref="HcsSandboxEngine"/>'s compute-system handle
/// lifetime (M1) and runtime reporting (N8), using fakes for the native HCS
/// surface.
/// </summary>
public sealed class HcsSandboxEngineTests
{
    [Fact]
    public async Task Start_holds_the_system_handle_until_stop()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out var checkpoints);

        var id = SandboxInstanceId.New();
        await engine.StartAsync(Request(id));

        // The handle must still be open after Start returns; otherwise
        // ShouldTerminateOnLastHandleClosed would kill the sandbox immediately.
        Assert.NotNull(hcs.LastHandle);
        Assert.False(hcs.LastHandle!.Disposed);
        Assert.Contains(id.ToString(), hcs.Started);

        await engine.StopAsync(id);

        // Stopping releases the last handle, which terminates the compute system.
        Assert.True(hcs.LastHandle.Disposed);
        Assert.Contains(id.ToString(), checkpoints.Released);
        Assert.DoesNotContain(id.ToString(), hcs.Terminated);
    }

    [Fact]
    public async Task Stop_terminates_a_system_this_engine_did_not_create()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _);

        var id = SandboxInstanceId.New();
        await engine.StopAsync(id);

        Assert.Contains(id.ToString(), hcs.Terminated);
    }

    [Fact]
    public async Task GetRuntime_reports_the_started_connection_settings()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _);

        var id = SandboxInstanceId.New();
        await engine.StartAsync(Request(id, rail: true, fullScreen: true));

        var runtime = await engine.GetRuntimeAsync(id);

        Assert.NotNull(runtime);
        Assert.True(runtime!.RailMode);
        Assert.True(runtime.FullScreen);
        Assert.Equal(id, runtime.SandboxId);
    }

    [Fact]
    public async Task Start_grants_vm_access_to_every_storage_file_including_the_base_disk()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _);

        var id = SandboxInstanceId.New();
        await engine.StartAsync(Request(id));

        var storage = Path.Combine(Path.GetTempPath(), "wsm-test-" + id);

        // The parent base copy was the file that was previously never granted;
        // under the per-user Documents location its absence made Start fail
        // with 0xC03A0016.
        Assert.Contains(Path.Combine(storage, "sandbox.base.vhdx"), hcs.Granted);
        Assert.Contains(Path.Combine(storage, "sandbox.vhdx"), hcs.Granted);
        Assert.Contains(Path.Combine(storage, "sandbox.vmgs"), hcs.Granted);

        // The crash-dump placeholder is covered by directory enumeration.
        Assert.Contains(Path.Combine(storage, id + ".dmp"), hcs.Granted);

        // The directory itself is granted best-effort.
        Assert.Contains(storage, hcs.Granted);
    }

    [Fact]
    public async Task Start_surfaces_a_failed_storage_grant_and_releases_the_handle()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _);

        var id = SandboxInstanceId.New();
        var baseDisk = Path.Combine(Path.GetTempPath(), "wsm-test-" + id, "sandbox.base.vhdx");
        hcs.FailGrantFor = path => string.Equals(path, baseDisk, StringComparison.OrdinalIgnoreCase);

        var ex = await Assert.ThrowsAsync<SandboxBackendException>(() => engine.StartAsync(Request(id)));

        Assert.Contains("sandbox.base.vhdx", ex.Message);
        Assert.NotNull(hcs.LastHandle);
        Assert.True(hcs.LastHandle!.Disposed);
        Assert.DoesNotContain(id.ToString(), hcs.Started);
    }

    [Fact]
    public async Task DeleteData_removes_storage_and_snapshot_directories()
    {
        var engine = CreateEngine(new FakeHcsClient(), out _);
        var id = SandboxInstanceId.New();
        var key = id.ToString();

        var storage = Path.Combine(Path.GetTempPath(), "wsm-test-" + key);
        var snapshots = Path.Combine(Path.GetTempPath(), key);
        Directory.CreateDirectory(storage);
        Directory.CreateDirectory(snapshots);
        await File.WriteAllTextAsync(Path.Combine(storage, "sandbox.vhdx"), "data");

        await engine.DeleteDataAsync(id);

        Assert.False(Directory.Exists(storage));
        Assert.False(Directory.Exists(snapshots));
    }

    [Fact]
    public async Task DeleteData_is_best_effort_when_nothing_exists()
    {
        var engine = CreateEngine(new FakeHcsClient(), out _);

        // Must not throw when the instance never wrote anything.
        await engine.DeleteDataAsync(SandboxInstanceId.New());
    }

    private static HcsSandboxEngine CreateEngine(FakeHcsClient hcs, out FakeCheckpoints checkpoints)
    {
        checkpoints = new FakeCheckpoints();
        return new HcsSandboxEngine(
            hcs,
            new FakeLayout(),
            new FakeJson(),
            new FakeStorage(),
            checkpoints,
            new FakeElevation(),
            NullLogger<HcsSandboxEngine>.Instance);
    }

    private static SandboxStartRequest Request(SandboxInstanceId id, bool rail = false, bool fullScreen = false)
        => new()
        {
            Id = id,
            Recipe = SandboxRecipe.Default with { RailMode = rail, FullScreen = fullScreen },
        };

    private sealed class TrackingHandle : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeHcsClient : IHcsClient
    {
        public List<string> Started { get; } = new();

        public List<string> Terminated { get; } = new();

        public List<string> Granted { get; } = new();

        /// <summary>When set, a grant whose path matches throws.</summary>
        public Func<string, bool>? FailGrantFor { get; set; }

        public TrackingHandle? LastHandle { get; private set; }

        public IReadOnlyList<HcsComputeSystemInfo> List(string? query = null)
            => Array.Empty<HcsComputeSystemInfo>();

        public HcsComputeSystemInfo? Get(string id) => new() { Id = id };

        public IDisposable Create(string id, string configuration)
        {
            LastHandle = new TrackingHandle();
            return LastHandle;
        }

        public void Start(string id) => Started.Add(id);

        public void Shutdown(string id)
        {
        }

        public void Terminate(string id) => Terminated.Add(id);

        public string? Save(string id, string options) => null;

        public void CreateEmptyGuestStateFile(string path)
        {
        }

        public void CreateEmptyRuntimeStateFile(string path)
        {
        }

        public void GrantVmAccess(string id, string path)
        {
            if (FailGrantFor?.Invoke(path) == true)
            {
                throw new InvalidOperationException($"Access denied for '{path}'.");
            }

            Granted.Add(path);
        }
    }

    private sealed class FakeLayout : IHcsHostLayout
    {
        public string PlatformContainersFolder => Path.GetTempPath();

        public string LayersFolder => Path.GetTempPath();

        public string BaseImagesFolder => Path.GetTempPath();

        public string PlatformContainerStoragesFolder => Path.GetTempPath();

        public string SnapshotsFolder => Path.GetTempPath();

        public string SandboxDataFolder => Path.GetTempPath();

        public string GetStorageDirectory(string id) => Path.Combine(Path.GetTempPath(), "wsm-test-" + id);

        public IReadOnlyList<HcsLayerInfo> ListLayers() => Array.Empty<HcsLayerInfo>();

        public string? ResolveBaseLayerPath() => Path.GetTempPath();
    }

    private sealed class FakeStorage : ISandboxStorageProvisioner
    {
        public StorageLayout Provision(string storageDirectory, uint diskSizeMb = 0)
        {
            Directory.CreateDirectory(storageDirectory);
            var disk = Path.Combine(storageDirectory, "sandbox.vhdx");
            var guestState = Path.Combine(storageDirectory, "sandbox.vmgs");
            var baseDisk = Path.Combine(storageDirectory, "sandbox.base.vhdx");
            File.WriteAllText(disk, "disk");
            File.WriteAllText(guestState, "vmgs");
            File.WriteAllText(baseDisk, "base");
            return new StorageLayout(storageDirectory, disk, guestState, baseDisk);
        }
    }

    private sealed class FakeJson : ISandboxVmJsonBuilder
    {
        public string Build(HcsSandboxDefinition definition) => "{}";
    }

    private sealed class FakeCheckpoints : ICheckpointService
    {
        public List<string> Released { get; } = new();

        public SandboxCheckpoint Save(string systemId, string name, SandboxCheckpointType type)
            => throw new NotSupportedException();

        public IReadOnlyList<SandboxCheckpoint> List(string systemId)
            => Array.Empty<SandboxCheckpoint>();

        public string Restore(HcsSandboxDefinition definition, SandboxCheckpoint checkpoint)
            => throw new NotSupportedException();

        public void Release(string systemId) => Released.Add(systemId);
    }

    private sealed class FakeElevation : IElevationService
    {
        public bool IsElevated => true;

        public bool TryRelaunchElevated(string? arguments = null) => false;
    }
}
