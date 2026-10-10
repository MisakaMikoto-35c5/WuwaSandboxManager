using Microsoft.Extensions.Logging;
using WindowsSandboxManager.Domain.Abstractions;
using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;

namespace WindowsSandboxManager.Infrastructure.Hcs;

/// <summary>
/// Sandbox backend driven directly by the Host Compute Service
/// (<c>computecore.dll</c>). Unlike the gRPC control plane, this backend:
/// <list type="bullet">
///   <item>can run <b>multiple instances concurrently</b> (no licensing gate);</item>
///   <item>can set the <b>virtual processor count</b>;</item>
///   <item>can point the <b>base image layer</b> and <b>user-data disk</b> at custom paths;</item>
///   <item>can create and restore <b>checkpoints</b>.</item>
/// </list>
/// It requires an elevated process (HCS returns <c>0x8037011B</c> otherwise).
/// </summary>
public sealed class HcsSandboxBackend : ISandboxBackend
{
    private readonly HcsService _hcs;
    private readonly HcsHostLayout _layout;
    private readonly HcsStorageManager _storage;
    private readonly HcsCheckpointManager _checkpoints;
    private readonly ILogger<HcsSandboxBackend> _logger;

    public HcsSandboxBackend(
        HcsService hcs,
        HcsHostLayout layout,
        HcsStorageManager storage,
        HcsCheckpointManager checkpoints,
        ILogger<HcsSandboxBackend> logger)
    {
        _hcs = hcs;
        _layout = layout;
        _storage = storage;
        _checkpoints = checkpoints;
        _logger = logger;
    }

    public string Name => "HCS (computecore)";

    public bool IsAvailable => HcsHostLayout.IsElevated;

    public SandboxBackendCapabilities Capabilities => new()
    {
        SupportsMultipleInstances = true,
        SupportsCpuConfiguration = true,
        SupportsRuntimeFolderMapping = false,
        SupportsProcessExecution = false,
        SupportsEnumeration = true,
        SupportsReboot = false,
        SupportsCheckpoints = true,
        SupportsCustomStorageLocation = true,
        SupportsCustomBaseImage = true,
        RequiresElevation = true,
    };

    public Task<IReadOnlyList<SandboxInstanceId>> EnumerateRunningAsync(CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        var ids = _hcs.List()
            .Select(s => Guid.TryParse(s.Id, out var id) ? new SandboxInstanceId(id) : (SandboxInstanceId?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();

        return Task.FromResult<IReadOnlyList<SandboxInstanceId>>(ids);
    }

    public Task<SandboxRuntimeInfo> StartAsync(
        SandboxRecipe recipe,
        SandboxInstanceId id,
        CancellationToken cancellationToken = default)
    {
        EnsureElevated();

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

        var json = HcsSandboxJson.Build(definition);
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

    public Task ShutdownAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        _hcs.Terminate(id.ToString());
        return Task.CompletedTask;
    }

    public Task MapFolderAsync(SandboxInstanceId id, MappedFolder folder, CancellationToken cancellationToken = default)
        => throw new SandboxNotSupportedException(
            "The HCS backend maps folders at creation time. Recreate the sandbox with the folder in its recipe.");

    public Task<int> ExecuteAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default)
        => throw new SandboxNotSupportedException(
            "The HCS backend does not implement in-guest process execution; use the gRPC backend for that.");

    public Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        var info = _hcs.Get(id.ToString());
        if (info?.Raw is { } raw &&
            raw.TryGetProperty("Networking", out var networking) &&
            networking.TryGetProperty("Statistics", out _))
        {
            // HCS does not expose the guest IP directly; it is tracked by HNS.
        }

        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task<SandboxRuntimeInfo?> GetRuntimeInfoAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
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

    public async Task<SandboxTerminationReason> WaitForTerminationAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        while (!cancellationToken.IsCancellationRequested)
        {
            var info = _hcs.Get(id.ToString());
            if (info is null || !info.IsRunning)
            {
                return SandboxTerminationReason.UserInitiatedShutdown;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        return SandboxTerminationReason.Unknown;
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

    private static void EnsureElevated()
    {
        if (!HcsHostLayout.IsElevated)
        {
            throw new SandboxNotSupportedException(HcsErrors.ElevationMessage);
        }
    }
}
