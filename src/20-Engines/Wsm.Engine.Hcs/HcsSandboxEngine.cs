using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Checkpoint;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Sandbox;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Storage;
using Wsm.Host.Elevation;

namespace Wsm.Engine.Hcs;

/// <summary>
/// Sandbox engine driven directly by the Host Compute Service
/// (<c>computecore.dll</c>). Unlike the gRPC control plane, this engine:
/// <list type="bullet">
///   <item>can run <b>multiple instances concurrently</b> (no licensing gate);</item>
///   <item>can set the <b>virtual processor count</b>;</item>
///   <item>can point the <b>base image layer</b> and <b>user-data disk</b> at custom paths;</item>
///   <item>can create and restore <b>checkpoints</b>.</item>
/// </list>
/// It requires an elevated process (HCS returns <c>0x8037011B</c> otherwise).
/// Runtime folder mapping and in-guest process execution are not supported and
/// fall back to the <see cref="ISandboxEngine"/> default-throwing members.
/// </summary>
[ExposeServices(typeof(ISandboxEngine))]
internal sealed class HcsSandboxEngine : ISandboxEngine, ISingletonDependency
{
    private const string ElevationMessage =
        "The Host Compute Service (HCS) requires an elevated process. " +
        "Run WindowsSandboxManager as Administrator (or as a member of the Hyper-V Administrators group).";

    private readonly IHcsClient _hcs;
    private readonly IHcsHostLayout _layout;
    private readonly ISandboxVmJsonBuilder _json;
    private readonly ISandboxStorageProvisioner _storage;
    private readonly ICheckpointService _checkpoints;
    private readonly IElevationService _elevation;
    private readonly ILogger<HcsSandboxEngine> _logger;

    /// <summary>
    /// Live compute systems created by this engine, keyed by compute-system id.
    /// The native <c>HCS_SYSTEM</c> handle <b>must</b> be held for the instance's
    /// whole lifetime: the configuration sets
    /// <c>ShouldTerminateOnLastHandleClosed=true</c>, so dropping the handle (as
    /// a <c>using</c> in <see cref="StartAsync"/> would) terminates the sandbox
    /// the instant the start call returns. The handle is released in
    /// <see cref="StopAsync"/>, which is what terminates the compute system.
    /// The runtime info is cached alongside so <see cref="GetRuntimeAsync"/> can
    /// report the real connection settings (RAIL mode, full-screen, title, …)
    /// rather than a synthesized default.
    /// </summary>
    private readonly ConcurrentDictionary<string, LiveSystem> _systems =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record LiveSystem(IDisposable Handle, SandboxRuntimeInfo Runtime);

    public HcsSandboxEngine(
        IHcsClient hcs,
        IHcsHostLayout layout,
        ISandboxVmJsonBuilder json,
        ISandboxStorageProvisioner storage,
        ICheckpointService checkpoints,
        IElevationService elevation,
        ILogger<HcsSandboxEngine> logger)
    {
        _hcs = hcs;
        _layout = layout;
        _json = json;
        _storage = storage;
        _checkpoints = checkpoints;
        _elevation = elevation;
        _logger = logger;
    }

    public string Name => "HCS (computecore)";

    public SandboxEngineKind Kind => SandboxEngineKind.HcsSandbox;

    public bool IsAvailable => _elevation.IsElevated;

    public SandboxEngineCapabilities Capabilities => new()
    {
        SupportsMultipleInstances = true,
        SupportsCpuConfiguration = true,
        SupportsCustomStorageLocation = true,
        SupportsCustomBaseImage = true,
        SupportsCheckpoints = true,
        SupportsRuntimeFolderMapping = false,
        SupportsProcessExecution = false,
        SupportsEnumeration = true,
        RequiresElevation = true,
    };

    public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        var ids = _hcs.List()
            .Select(s => Guid.TryParse(s.Id, out var id) ? new SandboxInstanceId(id) : (SandboxInstanceId?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();

        return Task.FromResult<IReadOnlyList<SandboxInstanceId>>(ids);
    }

    public Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        var id = request.Id;
        var recipe = request.Recipe;

        var baseLayer = _layout.ResolveBaseLayerPath()
            ?? throw new SandboxNotSupportedException(
                "No usable base OS image layer was found under " +
                $"{_layout.LayersRoot}. Start Windows Sandbox once so the base layer is provisioned.");

        var storageDirectory = recipe.Storage.HasCustomLocation
            ? recipe.Storage.StoragePath!
            : _layout.GetStorageDirectory(id.ToString());

        StorageLayout storage;
        try
        {
            storage = _storage.Provision(storageDirectory, recipe.Storage.DiskSizeMb);
        }
        catch (SandboxBackendException ex)
        {
            throw new SandboxBackendException(
                $"Failed to provision the user-data storage for sandbox {id} at " +
                $"'{storageDirectory}': {ex.Message}",
                ex.HResult,
                ex);
        }

        var definition = new HcsSandboxDefinition
        {
            Id = id.ToString(),
            BaseLayerPath = baseLayer,
            StorageDiskPath = storage.DiskPath,
            GuestStatePath = storage.GuestStatePath,
            DumpFilePath = EnsureDumpPlaceholder(storageDirectory, id.ToString()),
            MemoryMb = (uint)recipe.Memory.MemoryMb,
            CpuCount = recipe.Cpu.CoreCount,
            EnableVirtualGpu = recipe.VirtualGpu != VirtualGpuMode.Disabled,
            EnableNetworking = recipe.Networking != NetworkingMode.Disabled,
            MappedFolders = recipe.MappedFolders
                .Select(f => new HcsMappedFolder(f.HostFolder, f.SandboxFolder, f.ReadOnly))
                .ToArray(),
            RdpNamedPipe = $@"\\.\pipe\{id}",
        };

        var json = _json.Build(definition);
        _logger.LogInformation(
            "Creating HCS compute system {SandboxId} (base={BaseLayer}, storage={StorageDirectory}, " +
            "memory={MemoryMb}MB, cpus={CpuCount})",
            id, baseLayer, storage.Directory, definition.MemoryMb, definition.CpuCount);

        var system = _hcs.Create(id.ToString(), json);

        try
        {
            // The compute system runs as a different identity than this process,
            // so grant it explicit access to the freshly-provisioned storage.
            // Best effort: storage created under ProgramData normally inherits
            // SYSTEM's full control already, and a genuine access problem will
            // surface clearly from Start.
            GrantVmAccessQuietly(id.ToString(), storage.DiskPath);
            GrantVmAccessQuietly(id.ToString(), storage.GuestStatePath);
            if (!string.IsNullOrWhiteSpace(definition.DumpFilePath))
            {
                GrantVmAccessQuietly(id.ToString(), definition.DumpFilePath);
            }

            _hcs.Start(id.ToString());
        }
        catch (SandboxBackendException ex)
        {
            system.Dispose();
            _logger.LogError(ex, "HCS rejected or failed to start compute system {SandboxId}", id);
            throw new SandboxBackendException(
                $"HCS rejected the configuration or failed to start sandbox {id}: {ex.Message}",
                ex.HResult,
                ex);
        }
        catch (SandboxDomainException)
        {
            system.Dispose();
            throw;
        }
        catch
        {
            system.Dispose();
            throw;
        }

        var runtime = new SandboxRuntimeInfo
        {
            SandboxId = id,
            VmId = id.Value,
            Transport = RdpTransportKind.NamedPipe,
            RailMode = recipe.RailMode,
            FullScreen = recipe.FullScreen,
            WindowTitle = recipe.Name.Value,
            UserName = "WDAGUtilityAccount",
            Redirections = recipe.Redirections,
        };

        // Keep the handle (and the runtime info) for the instance's lifetime;
        // releasing it is what stops the sandbox.
        RememberSystem(id.ToString(), system, runtime);

        return Task.FromResult(runtime);
    }

    public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        var key = id.ToString();

        // Disposing the held HCS_SYSTEM handle closes the last handle to the
        // compute system; with ShouldTerminateOnLastHandleClosed=true that
        // terminates it. Fall back to an explicit terminate for systems this
        // engine did not create in this process.
        if (_systems.TryRemove(key, out var live))
        {
            live.Handle.Dispose();
        }
        else
        {
            _hcs.Terminate(key);
        }

        // A checkpoint-restored system holds its handle in the checkpoint
        // service; release it so the system can terminate.
        _checkpoints.Release(key);

        return Task.CompletedTask;
    }

    public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        // Prefer the runtime captured at start: it carries the real RAIL mode,
        // full-screen flag, title and redirections. A system we did not start in
        // this process can only be described with HCS defaults.
        if (_systems.TryGetValue(id.ToString(), out var live))
        {
            return Task.FromResult<SandboxRuntimeInfo?>(live.Runtime);
        }

        var info = _hcs.Get(id.ToString());
        if (info is null)
        {
            return Task.FromResult<SandboxRuntimeInfo?>(null);
        }

        return Task.FromResult<SandboxRuntimeInfo?>(new SandboxRuntimeInfo
        {
            SandboxId = id,
            VmId = id.Value,
            Transport = RdpTransportKind.NamedPipe,
        });
    }

    public Task<SandboxCheckpoint> SaveCheckpointAsync(
        SandboxInstanceId id,
        string name,
        SandboxCheckpointType type,
        CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        return Task.FromResult(_checkpoints.Save(id.ToString(), name, type));
    }

    public Task<IReadOnlyList<SandboxCheckpoint>> ListCheckpointsAsync(
        SandboxInstanceId id,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_checkpoints.List(id.ToString()));

    public Task<Guid> RestoreCheckpointAsync(
        SandboxInstanceId id,
        SandboxCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        var baseLayer = _layout.ResolveBaseLayerPath()
            ?? throw new SandboxNotSupportedException("No usable base OS image layer was found.");

        var storageDirectory = _layout.GetStorageDirectory(id.ToString());
        var storage = _storage.Provision(storageDirectory);

        var definition = new HcsSandboxDefinition
        {
            Id = id.ToString(),
            BaseLayerPath = baseLayer,
            StorageDiskPath = storage.DiskPath,
            GuestStatePath = storage.GuestStatePath,
        };

        var newId = _checkpoints.Restore(definition, checkpoint);
        return Task.FromResult(Guid.Parse(newId));
    }

    private void EnsureElevated()
    {
        if (!_elevation.IsElevated)
        {
            throw new SandboxNotSupportedException(ElevationMessage);
        }
    }

    /// <summary>
    /// Creates the zero-byte guest crash dump placeholder HCS expects. The
    /// "Microsoft Guest Crash Dump" device opens this file at Start and fails
    /// with <c>0x80070002</c> when it is absent, so the file (and its directory)
    /// must already exist. Returns the resolved path.
    /// </summary>
    private static string EnsureDumpPlaceholder(string storageDirectory, string id)
    {
        var dumpPath = Path.Combine(storageDirectory, $"{id}.dmp");
        var directory = Path.GetDirectoryName(dumpPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(dumpPath))
        {
            using var _ = File.Create(dumpPath);
        }

        return dumpPath;
    }

    private void RememberSystem(string key, IDisposable handle, SandboxRuntimeInfo runtime)
    {
        var live = new LiveSystem(handle, runtime);

        // A duplicate id should not occur (HCS rejects it), but never leak the
        // previous handle if it does.
        if (_systems.TryRemove(key, out var stale))
        {
            try
            {
                stale.Handle.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Replacing the handle for {SandboxId} failed.", key);
            }
        }

        _systems[key] = live;
    }

    private void GrantVmAccessQuietly(string id, string path)
    {
        try
        {
            _hcs.GrantVmAccess(id, path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Granting VM access to '{Path}' for {SandboxId} failed (continuing).", path, id);
        }
    }
}
