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

        var storage = _storage.Provision(storageDirectory);

        var definition = new HcsSandboxDefinition
        {
            Id = id.ToString(),
            BaseLayerPath = baseLayer,
            StorageDiskPath = storage.DiskPath,
            GuestStatePath = storage.GuestStatePath,
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
        _logger.LogInformation("Creating HCS compute system {SandboxId} (base={BaseLayer})", id, baseLayer);

        using var system = _hcs.Create(id.ToString(), json);
        _hcs.Start(id.ToString());

        return Task.FromResult(new SandboxRuntimeInfo
        {
            SandboxId = id,
            VmId = id.Value,
            Transport = RdpTransportKind.NamedPipe,
            RailMode = recipe.RailMode,
            FullScreen = recipe.FullScreen,
            WindowTitle = recipe.Name.Value,
            UserName = "WDAGUtilityAccount",
            Redirections = recipe.Redirections,
        });
    }

    public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        _hcs.Terminate(id.ToString());
        return Task.CompletedTask;
    }

    public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
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
}
