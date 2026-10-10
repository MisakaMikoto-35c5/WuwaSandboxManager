using System.Text.Json;
using Wsm.Domain.ValueObjects;
using Wsm.Hcs.Checkpoint;
using Wsm.Hcs.Checkpoint.Internal;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Sandbox;
using Wsm.Hcs.Layout;
using Xunit;

namespace Wsm.Tests.Hcs;

/// <summary>
/// Covers the per-system scoping of <see cref="CheckpointService"/>: checkpoints
/// saved for one compute system are invisible to another, and non-GUID
/// directories under a system's snapshot folder are skipped.
/// </summary>
public sealed class CheckpointScopingTests
{
    [Fact]
    public void Checkpoints_are_scoped_to_their_owning_system_and_skip_non_guid_directories()
    {
        var snapshotsRoot = Path.Combine(Path.GetTempPath(), "wsm-cp-" + Guid.NewGuid());
        var service = new CheckpointService(
            new StubHcsClient(),
            new StubLayout(snapshotsRoot),
            new StubJsonBuilder());

        try
        {
            var systemA = Guid.NewGuid().ToString();
            var systemB = Guid.NewGuid().ToString();

            var saved = service.Save(systemA, "cp1", SandboxCheckpointType.FullState);

            // Scoping: another system's snapshot folder is invisible.
            Assert.Empty(service.List(systemB));

            // A non-GUID directory under the owning system is skipped.
            var bogusDirectory = Path.Combine(snapshotsRoot, systemA, "not-a-guid");
            Directory.CreateDirectory(bogusDirectory);
            File.WriteAllText(Path.Combine(bogusDirectory, "SnapshotSavedState.vmrs"), string.Empty);

            var listed = service.List(systemA);
            var single = Assert.Single(listed);
            Assert.Equal(saved.Id, single.Id);
        }
        finally
        {
            if (Directory.Exists(snapshotsRoot))
            {
                Directory.Delete(snapshotsRoot, recursive: true);
            }
        }
    }

    private sealed class StubLayout : IHcsHostLayout
    {
        public StubLayout(string snapshotsRoot)
        {
            DataRoot = Path.GetDirectoryName(snapshotsRoot) ?? snapshotsRoot;
            SnapshotsRoot = snapshotsRoot;
        }

        public string DataRoot { get; }

        public string LayersRoot => Path.Combine(DataRoot, "Layers");

        public string BaseImagesRoot => Path.Combine(DataRoot, "BaseImages");

        public string StorageRoot => Path.Combine(DataRoot, "ContainerStorages");

        public string SnapshotsRoot { get; }

        public string GetStorageDirectory(string id) => Path.Combine(StorageRoot, id);

        public IReadOnlyList<HcsLayerInfo> ListLayers() => Array.Empty<HcsLayerInfo>();

        public string? ResolveBaseLayerPath() => null;
    }

    private sealed class StubJsonBuilder : ISandboxVmJsonBuilder
    {
        public string Build(HcsSandboxDefinition definition) => "{}";
    }

    private sealed class StubHcsClient : IHcsClient
    {
        public IReadOnlyList<HcsComputeSystemInfo> List(string? query = null)
            => Array.Empty<HcsComputeSystemInfo>();

        public HcsComputeSystemInfo? Get(string id) => null;

        public IDisposable Create(string id, string configuration) => new NoopDisposable();

        public void Start(string id)
        {
        }

        public void Shutdown(string id)
        {
        }

        public void Terminate(string id)
        {
        }

        public string? Save(string id, string options)
        {
            // Mirror the HCS save contract: the options carry the state file path
            // that the service later enumerates.
            using var document = JsonDocument.Parse(options);
            var statePath = document.RootElement.GetProperty("SaveStateFilePath").GetString();
            if (!string.IsNullOrEmpty(statePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
                File.WriteAllText(statePath, string.Empty);
            }

            return null;
        }

        public void CreateEmptyGuestStateFile(string path)
        {
        }

        public void CreateEmptyRuntimeStateFile(string path)
        {
        }

        public void GrantVmAccess(string id, string path)
        {
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
