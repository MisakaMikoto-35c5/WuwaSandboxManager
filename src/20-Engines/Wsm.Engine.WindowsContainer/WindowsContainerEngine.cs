using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engine.WindowsContainer.Internal;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Container;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Network;
using Wsm.Hcs.Process;
using Wsm.Hcs.Storage;
using Wsm.Host.Elevation;

namespace Wsm.Engine.WindowsContainer;

/// <summary>
/// The Windows container engine: HCS <b>process-isolated</b> (Argon) containers.
/// <para>
/// This is the lightweight isolation tier — the Windows analogue of the
/// <c>processcontainer</c> backend in Microsoft's MXC (Microsoft eXecution
/// Containers). No virtual machine is booted; the container's <c>C:\</c> is a
/// per-container writable <b>scratch layer</b> with a read-only base image
/// layer unioned beneath it through WCIFS:
/// </para>
/// <list type="number">
///   <item>HNS creates a namespace + NAT endpoint and adds the endpoint to the
///   namespace (the container joins the namespace to get its NIC);</item>
///   <item><see cref="IWcowLayerService"/> creates the scratch layer from the
///   base image (<c>CreateSandboxLayer</c>), mounts it (<c>ActivateLayer</c>) and
///   unions the image beneath it (<c>PrepareLayer</c>);</item>
///   <item>HCS creates and starts the container with
///   <c>Storage.Path</c> = the scratch volume and <c>Storage.Layers</c> = the
///   image.</item>
/// </list>
/// <para>
/// Process isolation requires the image build to match the host build, <b>except
/// for a Windows Server 2022/2025 image</b>, which a Windows 11 host may run
/// despite the mismatch. <see cref="WindowsContainerEngineOptions.BaseImageLayerPath"/>
/// selects the image; when unset the newest registered Windows Sandbox image
/// layer is used (a client image, so it only works on a matching host).
/// </para>
/// Like the HCS engine it requires an elevated process (<c>0x8037011B</c>
/// otherwise).
/// </summary>
[ExposeServices(typeof(ISandboxEngine), typeof(IContainerFileSystemAccess))]
internal sealed class WindowsContainerEngine : ISandboxEngine, IContainerFileSystemAccess, ISingletonDependency
{
    private const string ElevationMessage =
        "The Windows container engine drives the Host Compute Service (HCS) and requires an " +
        "elevated process. Run WindowsSandboxManager as Administrator (or as a member of the " +
        "Hyper-V Administrators group).";

    /// <summary>Host services a container needs before it can be created.</summary>
    private static readonly string[] RequiredHostServices = ["vmcompute", "hns"];

    private static readonly Regex LayerIdPattern = new(
        @"[\\/]Layers[\\/](?<id>[0-9a-fA-F]{8}-[0-9a-fA-F-]{27})[\\/]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHcsClient _hcs;
    private readonly IHcsHostLayout _layout;
    private readonly IContainerJsonBuilder _json;
    private readonly IComputeProcessExecutor _process;
    private readonly IWcowLayerService _layers;
    private readonly IHnsNetworkService _network;
    private readonly IElevationService _elevation;
    private readonly IContainerIsolationPolicy _isolation;
    private readonly IOptions<WindowsContainerEngineOptions> _options;
    private readonly ILogger<WindowsContainerEngine> _logger;

    /// <summary>
    /// Live containers created by this engine, keyed by compute-system id. The
    /// native handle must be held for the instance's whole lifetime because the
    /// configuration sets <c>ShouldTerminateOnLastHandleClosed=true</c>;
    /// releasing it terminates the container. The scratch layer and the network
    /// namespace/endpoint are remembered so <see cref="StopAsync"/> can tear them
    /// down (HNS objects and mounted layers outlive the compute system).
    /// </summary>
    private readonly ConcurrentDictionary<string, LiveContainer> _containers =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record LiveContainer(
        IDisposable Handle,
        SandboxRuntimeInfo Runtime,
        string ScratchPath,
        IReadOnlyList<string> BaseLayers,
        string? NetworkNamespaceId,
        string? NetworkEndpointId,
        bool Adopted);

    public WindowsContainerEngine(
        IHcsClient hcs,
        IHcsHostLayout layout,
        IContainerJsonBuilder json,
        IComputeProcessExecutor process,
        IWcowLayerService layers,
        IHnsNetworkService network,
        IElevationService elevation,
        IContainerIsolationPolicy isolation,
        IOptions<WindowsContainerEngineOptions> options,
        ILogger<WindowsContainerEngine> logger)
    {
        _hcs = hcs;
        _layout = layout;
        _json = json;
        _process = process;
        _layers = layers;
        _network = network;
        _elevation = elevation;
        _isolation = isolation;
        _options = options;
        _logger = logger;
    }

    public string Name => "Windows Container (HCS)";

    public SandboxEngineKind Kind => SandboxEngineKind.WindowsContainer;

    /// <summary>
    /// Process isolation requires elevation <b>and</b> an image HCS accepts
    /// (see <see cref="IContainerIsolationPolicy"/>). When it is not offered the
    /// selector simply falls through to a VM-isolated engine, which is always
    /// available.
    /// </summary>
    public bool IsAvailable => _elevation.IsElevated && _isolation.Evaluate().IsSupported;

    public SandboxEngineCapabilities Capabilities => new()
    {
        SupportsMultipleInstances = true,
        SupportsCpuConfiguration = true,
        SupportsCustomStorageLocation = true,
        // The base image is chosen through engine options, not the recipe.
        SupportsCustomBaseImage = false,
        SupportsCheckpoints = false,
        // Folders are bound at creation time (MappedDirectories); HCS cannot add
        // a mapping to an already-running container.
        SupportsRuntimeFolderMapping = false,
        SupportsProcessExecution = true,
        SupportsEnumeration = true,
        RequiresElevation = true,
    };

    public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        // Only containers belong to this engine; VMs are the HCS engine's.
        var ids = _hcs.List()
            .Where(system => system.IsContainer)
            .Select(system => Guid.TryParse(system.Id, out var id) ? new SandboxInstanceId(id) : (SandboxInstanceId?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();

        return Task.FromResult<IReadOnlyList<SandboxInstanceId>>(ids);
    }

    public async Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        var id = request.Id;
        var recipe = request.Recipe;
        var key = id.ToString();

        EnsureHostServices();

        var baseLayers = ResolveBaseLayers();

        // 1. Networking: create the namespace + endpoint and attach the endpoint
        // to the namespace so the container's NIC is present from the start.
        string? namespaceId = null;
        string? endpointId = null;
        if (recipe.Networking != NetworkingMode.Disabled
            && await TrySetupNetworkingAsync(id, cancellationToken) is ({ } ns, { } ep))
        {
            namespaceId = ns;
            endpointId = ep;
        }

        // 2. The scratch layer: created from the image, mounted and prepared so
        // it becomes a \\?\Volume{GUID}\ the container can use as C:.
        var storageDirectory = recipe.Storage.HasCustomLocation
            ? recipe.Storage.StoragePath!
            : _layout.GetStorageDirectory(key);
        var scratchPath = Path.Combine(storageDirectory, "scratch");

        // A previous run of this container may have left its scratch layer mounted: a process
        // killed instead of stopped never runs the release path below, and the layer then belongs
        // to a compute system that no longer exists. Releasing it first turns the ordinary
        // restart case into a clean provision; when even that is refused (an orphaned layer),
        // ProvisionScratchLayer steps to a fresh path (see its remarks for the measurement).
        await ReleaseScratchAsync(scratchPath, baseLayers);

        string mountPath;
        try
        {
            var provisioned = ScratchLayerProvisioner.Provision(_layers, scratchPath, baseLayers, out var attempts);
            scratchPath = provisioned.Path;
            mountPath = provisioned.MountPath;
            if (attempts.Count > 1)
            {
                _logger.LogWarning(
                    "The scratch layer for {SandboxId} moved to '{Scratch}' after: {Reasons}",
                    id,
                    scratchPath,
                    string.Join("; ", attempts.Take(attempts.Count - 1).Select(a => $"{a.Path}: {a.Reason}")));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision the container scratch layer for {SandboxId}.", id);
            await CleanupNetworkingQuietlyAsync(namespaceId, endpointId);
            throw new SandboxBackendException(
                $"Failed to provision the container scratch layer for {id}: {ex.Message}", 0, ex);
        }

        if (!mountPath.EndsWith('\\'))
        {
            mountPath += '\\';
        }

        var definition = new ContainerDefinition
        {
            Id = key,
            ScratchMountPath = mountPath,
            Layers = baseLayers
                .Select(layer => new ContainerLayer(_layers.GetLayerId(layer).ToString(), layer))
                .ToArray(),
            HostName = ToHostName(recipe.Name.Value, key),
            MemoryMb = (ulong)recipe.Memory.MemoryMb,
            CpuCount = (uint)recipe.Cpu.CoreCount,
            NetworkNamespace = namespaceId,
            MappedFolders = recipe.MappedFolders
                .Select(folder => new ContainerMappedFolder(
                    folder.HostFolder,
                    ResolveContainerPath(folder),
                    folder.ReadOnly))
                .ToArray(),
        };

        var json = _json.Build(definition);
        _logger.LogInformation(
            "Creating Windows container {SandboxId} (image={BaseLayer}, scratch={Scratch}, " +
            "memory={MemoryMb}MB, cpus={CpuCount}, namespace={Namespace})",
            id, baseLayers[0], scratchPath, definition.MemoryMb, definition.CpuCount, namespaceId);

        IDisposable system;
        try
        {
            system = _hcs.Create(key, json);
            _hcs.Start(key);
        }
        catch (Exception ex)
        {
            await TeardownOnFailedStartAsync(key, scratchPath, namespaceId, endpointId, ex);
            throw;
        }

        var runtime = new SandboxRuntimeInfo
        {
            SandboxId = id,
            VmId = id.Value,
            Transport = RdpTransportKind.NamedPipe,
            RailMode = recipe.RailMode,
            RailProgram = recipe.ResolvedRailProgram,
            FullScreen = recipe.FullScreen,
            WindowTitle = recipe.Name.Value,
            UserName = "ContainerAdministrator",
            Redirections = recipe.Redirections,
        };

        RememberContainer(key, system, runtime, scratchPath, baseLayers, namespaceId, endpointId, adopted: false);

        // The image is stripped down; bring up the configured services. Best
        // effort: a service that cannot start is logged, not fatal.
        await EnsureContainerServicesAsync(id, _options.Value.RequiredServices, cancellationToken);

        // Opt-in desktop session (shell, auto-logon) and the experimental RDP
        // attempt. Also best effort: the container stays usable without either.
        if (_options.Value.Desktop.ShouldConfigureFor(recipe.Desktop))
        {
            await ConfigureDesktopAsync(id, _options.Value.Desktop, cancellationToken);

            // The first boot of a scratch cannot complete auto-logon: winlogon makes
            // its attempt seconds after smss starts, long before HCS exec can set the
            // SAM password, fails once and flips AutoAdminLogon to 0 (measured). The
            // password and every winlogon value live in the scratch layer, so a single
            // extra boot over the same scratch logs on by itself. The marker keeps the
            // restart to exactly one per scratch.
            var marker = Path.Combine(mountPath, "wsm", ".desktop-password-armed");
            if (!File.Exists(marker))
            {
                await File.WriteAllTextAsync(
                    marker, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
                _logger.LogInformation(
                    "Restarting container {SandboxId} once: the console session's auto-logon " +
                    "needs the account password persisted in the scratch layer before winlogon " +
                    "makes its attempt.",
                    id);

                _containers.TryRemove(key, out var firstBoot);
                try
                {
                    firstBoot?.Handle.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Disposing the first-boot handle for container {SandboxId} failed.", key);
                }

                // Closing the last handle asks HCS to tear the compute system down,
                // but the teardown is asynchronous: an immediate re-create fails with
                // "a compute system with that id already exists" (measured). Terminate
                // explicitly, then poll until the system is really gone.
                _hcs.Terminate(key);
                await WaitForComputeSystemGoneAsync(id, key, cancellationToken);

                system = _hcs.Create(key, json);
                _hcs.Start(key);
                RememberContainer(key, system, runtime, scratchPath, baseLayers, namespaceId, endpointId, adopted: false);
                await EnsureContainerServicesAsync(id, _options.Value.RequiredServices, cancellationToken);
            }
        }

        return runtime;
    }

    /// <summary>
    /// Polls HCS until the compute system no longer exists. Termination after the
    /// last handle closes is asynchronous; recreating the same id too soon fails
    /// with "already exists". Bounded so a stuck system cannot hang the start.
    /// </summary>
    private async Task WaitForComputeSystemGoneAsync(
        SandboxInstanceId id, string key, CancellationToken cancellationToken)
    {
        const int maxAttempts = 60; // 60 x 500ms = 30s
        for (var attempt = 1; ; attempt++)
        {
            if (_hcs.Get(key) is null)
            {
                return;
            }

            if (attempt >= maxAttempts)
            {
                _logger.LogWarning(
                    "Container {SandboxId} is still listed by HCS 30s after termination; recreating it may fail.",
                    id);
                return;
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    public async Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        var key = id.ToString();

        if (_containers.TryRemove(key, out var live))
        {
            // Disposing the held handle closes the last handle to the compute
            // system, which terminates it.
            try
            {
                live.Handle.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Disposing the handle for container {SandboxId} failed.", key);
            }
        }
        else
        {
            // A container this process did not create (adopted/foreign).
            _hcs.Terminate(key);
        }

        await ReleaseScratchAsync(live?.ScratchPath, live?.BaseLayers);
        await CleanupNetworkingQuietlyAsync(live?.NetworkNamespaceId, live?.NetworkEndpointId);
    }

    public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        if (_containers.TryGetValue(id.ToString(), out var live))
        {
            return Task.FromResult<SandboxRuntimeInfo?>(live.Runtime);
        }

        var info = _hcs.Get(id.ToString());
        if (info is null || !info.IsContainer)
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

    public Task<int> ExecuteAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        return _process.ExecuteAsync(id.ToString(), command.Command, ct: cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        // The container's endpoint is named deterministically; HNS records the
        // address it assigned there. (Matching on the namespace would need the
        // namespace id, which HNS keys differently from the compute-system id.)
        var endpoint = await _network.GetEndpointByNameAsync(EndpointName(id), cancellationToken);
        if (endpoint?.IpAddress is { Length: > 0 } address)
        {
            return [address];
        }

        return await _network.GetIpAddressesAsync(id.ToString(), cancellationToken);
    }

    /// <summary>
    /// Reclaims the container's on-disk footprint: its scratch layer and any
    /// leftover volume/files, but not the whole per-sandbox folder (which also
    /// holds the manifest). Best effort: a partial failure is logged, never
    /// thrown, so the caller's delete still completes. The container is expected
    /// to be stopped first.
    /// </summary>
    public Task DeleteDataAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var key = id.ToString();
        var scratch = Path.Combine(_layout.GetStorageDirectory(key), "scratch");

        try
        {
            if (Directory.Exists(scratch))
            {
                _layers.Unprepare(scratch);
                _layers.Deactivate(scratch);
                _layers.Destroy(scratch);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Destroying the container scratch layer for {SandboxId} failed.", key);
        }

        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
                _logger.LogInformation("Deleted container scratch '{Directory}' for container {SandboxId}.", scratch, key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Failed to delete container scratch '{Directory}' for {SandboxId} (continuing).", scratch, key);
        }

        return Task.CompletedTask;
    }

    private void EnsureElevated()
    {
        if (!_elevation.IsElevated)
        {
            throw new SandboxNotSupportedException(ElevationMessage);
        }
    }

    /// <summary>
    /// The read-only image layers the container is built from, base first. Uses
    /// <see cref="WindowsContainerEngineOptions.BaseImageLayerPath"/> when
    /// configured, otherwise the image the isolation policy selected (including
    /// imported ones), otherwise the newest registered Windows Sandbox image layer.
    /// </summary>
    private IReadOnlyList<string> ResolveBaseLayers()
    {
        // Prefer the configured option, then the environment override (the CLI
        // does not always load a config file, so the env var is the reliable
        // way to point the engine at an imported Windows Server image).
        var configured = _options.Value.BaseImageLayerPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable("WSM_CONTAINER_BASE_IMAGE");
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            var root = Path.GetFullPath(configured);
            if (!Directory.Exists(Path.Combine(root, "Files")))
            {
                throw new SandboxNotSupportedException(
                    $"The configured container base image layer '{root}' does not look like a WCOW layer " +
                    "(it has no 'Files' folder). Import a Windows Server 2022/2025 image first.");
            }

            return [root];
        }

        // The isolation policy also knows the images the user imported by hand
        // (default root or the legacy import folder); a supported pick wins over
        // the Windows Sandbox client layer below.
        var support = _isolation.Evaluate();
        if (support.IsSupported && !string.IsNullOrWhiteSpace(support.ImagePath))
        {
            var imported = Path.GetFullPath(support.ImagePath);
            if (Directory.Exists(Path.Combine(imported, "Files")))
            {
                return [imported];
            }
        }

        var layer = _layout.ResolveBaseImageLayer();
        var layerPath = layer?.Path;
        if (string.IsNullOrWhiteSpace(layerPath))
        {
            // Fall back to deriving the root from the VM-oriented resolver.
            var files = _layout.ResolveBaseLayerPath();
            layerPath = files is null ? null : Path.GetDirectoryName(files.TrimEnd('\\', '/'));
        }

        if (string.IsNullOrWhiteSpace(layerPath))
        {
            throw new SandboxNotSupportedException(
                "No usable base OS image layer was found. Set WindowsContainerEngineOptions.BaseImageLayerPath " +
                "to an imported Windows Server 2022/2025 image layer, or start Windows Sandbox once so the " +
                "shared minimal image is provisioned.");
        }

        _logger.LogWarning(
            "No container base image is configured; using the Windows Sandbox image layer '{Layer}'. That is a " +
            "client image, so HCS will reject it with HCS_E_IMAGE_MISMATCH unless its build matches the host's. " +
            "Configure WindowsContainerEngineOptions.BaseImageLayerPath with an imported Windows Server image " +
            "for a host/image build mismatch.",
            layerPath);

        return [layerPath];
    }

    /// <summary>
    /// Creates the HNS namespace and a NAT endpoint, adds the endpoint to the
    /// namespace, and returns both ids. A failure is logged and yields
    /// <c>null</c> so the container still starts (without a NIC).
    /// </summary>
    private async Task<(string? NamespaceId, string? EndpointId)?> TrySetupNetworkingAsync(
        SandboxInstanceId id, CancellationToken cancellationToken)
    {
        try
        {
            var networkId = await _network.GetOrCreateContainerNetworkIdAsync(cancellationToken);
            var namespaceId = await _network.CreateNamespaceAsync(cancellationToken);
            var endpointId = await _network.CreateNatEndpointAsync(networkId, EndpointName(id), cancellationToken);
            await _network.AddEndpointToNamespaceAsync(namespaceId, endpointId, cancellationToken);

            _logger.LogInformation(
                "Container {SandboxId} networking ready (network={NetworkId}, namespace={NamespaceId}, endpoint={EndpointId}).",
                id, networkId, namespaceId, endpointId);

            return (namespaceId, endpointId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Networking setup for container {SandboxId} failed; it starts without a NIC.", id);
            return null;
        }
    }

    private async Task TeardownOnFailedStartAsync(
        string key, string scratchPath, string? namespaceId, string? endpointId, Exception cause)
    {
        _logger.LogError(cause, "HCS rejected or failed to start container {SandboxId}", key);

        if (cause is SandboxBackendException { HResult: unchecked((int)0x80370101) })
        {
            _logger.LogError(
                "The container image's OS build does not match the host (HCS_E_IMAGE_MISMATCH). Configure a " +
                "Windows Server 2022/2025 image layer via WindowsContainerEngineOptions.BaseImageLayerPath.");
        }

        try
        {
            _hcs.Terminate(key);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Terminating failed container {SandboxId} failed.", key);
        }

        await ReleaseScratchAsync(scratchPath, baseLayers: null);
        await CleanupNetworkingQuietlyAsync(namespaceId, endpointId);
    }

    private async Task ReleaseScratchAsync(string? scratchPath, IReadOnlyList<string>? baseLayers)
    {
        if (string.IsNullOrWhiteSpace(scratchPath))
        {
            return;
        }

        try
        {
            _layers.Unprepare(scratchPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unpreparing scratch '{Scratch}' failed.", scratchPath);
        }

        try
        {
            _layers.Deactivate(scratchPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Deactivating scratch '{Scratch}' failed.", scratchPath);
        }

        await Task.CompletedTask;
    }

    private async Task CleanupNetworkingQuietlyAsync(string? namespaceId, string? endpointId)
    {
        if (!string.IsNullOrWhiteSpace(endpointId))
        {
            try
            {
                await _network.DeleteEndpointAsync(endpointId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Deleting endpoint {EndpointId} failed.", endpointId);
            }
        }

        if (!string.IsNullOrWhiteSpace(namespaceId))
        {
            try
            {
                await _network.DeleteNamespaceAsync(namespaceId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Deleting namespace {NamespaceId} failed.", namespaceId);
            }
        }
    }

    /// <summary>
    /// Makes sure <c>vmcompute</c> and <c>hns</c> are running. Without them the
    /// container cannot be created; a missing service is logged as a warning (the
    /// subsequent HCS call then fails with a precise error).
    /// </summary>
    private void EnsureHostServices()
    {
        foreach (var service in RequiredHostServices)
        {
            try
            {
                if (WindowsHostServices.TryEnsureRunning(service))
                {
                    _logger.LogInformation("Required host service '{Service}' is running.", service);
                }
                else
                {
                    _logger.LogWarning(
                        "Could not start required host service '{Service}'; container creation may fail.", service);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Probing required host service '{Service}' failed (continuing).", service);
            }
        }
    }

    private async Task EnsureContainerServicesAsync(
        SandboxInstanceId id,
        IReadOnlyList<string> services,
        CancellationToken cancellationToken)
    {
        foreach (var service in services)
        {
            if (string.IsNullOrWhiteSpace(service))
            {
                continue;
            }

            try
            {
                var exitCode = await _process.ExecuteAsync(
                    id.ToString(), $"sc.exe start {service}", ct: cancellationToken);

                if (exitCode == 0)
                {
                    _logger.LogInformation("Started service '{Service}' in container {SandboxId}.", service, id);
                }
                else
                {
                    _logger.LogWarning(
                        "Service '{Service}' in container {SandboxId} exited with code {ExitCode}.",
                        service, id, exitCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Failed to start service '{Service}' in container {SandboxId} (continuing).", service, id);
            }
        }
    }

    /// <inheritdoc />
    public string? GetContainerRoot(SandboxInstanceId id)
    {
        // The container's C:\ is its activated scratch layer, mounted as a host
        // volume; the host can therefore read and write files inside a running
        // container directly (no guest agent needed to get a file in).
        if (!_containers.TryGetValue(id.ToString(), out var live))
        {
            return null;
        }

        try
        {
            return _layers.GetMountPath(live.ScratchPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resolving the mount path of container {SandboxId} failed.", id);
            return null;
        }
    }

    /// <summary>
    /// Opt-in desktop session inside the container: <c>winlogon</c>'s shell, an
    /// optional auto-logon, and the experimental RDP attempt. Everything is best
    /// effort — a container whose image was not prepared for a console session
    /// simply has no desktop to log into, and that must not fail the start.
    /// </summary>
    private async Task ConfigureDesktopAsync(
        SandboxInstanceId id, ContainerDesktopOptions desktop, CancellationToken cancellationToken)
    {
        const string winlogon = @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

        // An explicitly enabled desktop with no configured password uses the shared default
        // that 'wsm image session' baked into the image: auto-logon only completes when the
        // two match (measured, doc section 11.7). The env-var switch counts the same as the
        // option (ShouldConfigure); without any desktop request, no password means no
        // auto-logon, exactly as before.
        var password = desktop.Password;
        if (string.IsNullOrEmpty(password) && desktop.ShouldConfigure)
        {
            password = ContainerDesktopSessionDefaults.DefaultPassword;
        }

        _logger.LogInformation(
            "Configuring a desktop session in container {SandboxId} (shell={Shell}, autoLogon={AutoLogon}).",
            id, desktop.Shell, !string.IsNullOrEmpty(password));

        // The container images ship no explorer.exe, so winlogon's default shell
        // cannot start; point it at something that is actually in the image.
        await RunInContainerAsync(
            id, RegAdd(winlogon, "Shell", desktop.Shell), cancellationToken);

        if (!string.IsNullOrEmpty(password))
        {
            // The image may carry a DefaultPassword from 'wsm image session'; the account's
            // runtime password must equal it or the auto-logon fails (the password baked into
            // the image cannot log on an account that does not have it). Set it first, then
            // the winlogon values. /ACTIVE:YES is belt and braces: the image's Administrator
            // can ship disabled.
            await RunInContainerAsync(
                id, $"net.exe user \"{desktop.UserName}\" \"{password}\"", cancellationToken);
            await RunInContainerAsync(
                id, $"net.exe user \"{desktop.UserName}\" /ACTIVE:YES", cancellationToken);

            await RunInContainerAsync(id, RegAdd(winlogon, "AutoAdminLogon", "1"), cancellationToken);
            await RunInContainerAsync(
                id, RegAdd(winlogon, "DefaultUserName", desktop.UserName), cancellationToken);
            await RunInContainerAsync(
                id, RegAdd(winlogon, "DefaultPassword", password), cancellationToken);
            // Without ForceAutoLogon winlogon can stay at LogonUI after the first attempt,
            // and in a container there is nobody to type credentials.
            await RunInContainerAsync(id, RegAdd(winlogon, "ForceAutoLogon", "1"), cancellationToken);
        }
        else
        {
            _logger.LogWarning(
                "Container {SandboxId} has no desktop password configured, so no auto-logon was written: the " +
                "console session stays on the logon screen (LogonUI). That is still a desktop, just not a " +
                "logged-in one.",
                id);
        }

        if (desktop.ShouldAttemptTcpRdp)
        {
            await AttemptTcpRdpAsync(id, cancellationToken);
        }
    }

    /// <summary>
    /// The experimental RDP attempt, kept because this branch records every knob
    /// that was tried. It writes the settings the Windows Sandbox guest uses (a
    /// <i>client VM</i>, where <c>termsrv</c> never takes its container path) and
    /// then reports the outcome honestly by probing 3389 from the host.
    /// <para>
    /// Expected result: no listener. Inside a container <c>termsrv</c> refuses to
    /// create the RDP listener (<c>WTSIsServerContainer</c> → "TS in container and
    /// so Remote Connection is not allow"); see
    /// <c>docs/investigation/13-container-desktop-and-rdp.md</c>.
    /// </para>
    /// </summary>
    private async Task AttemptTcpRdpAsync(SandboxInstanceId id, CancellationToken cancellationToken)
    {
        const string terminalServer = @"HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server";
        const string rdpTcp = @"HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";

        _logger.LogWarning(
            "Attempting to enable the RDP listener in container {SandboxId}. This is known not to work: termsrv " +
            "refuses to create an RDP listener when it detects a container. The attempt is recorded here so the " +
            "settings that were ruled out stay visible (docs/investigation/13-container-desktop-and-rdp.md).",
            id);

        foreach (var command in new[]
                 {
                     RegAdd(terminalServer, "ContainerMode", "0"),
                     RegAdd(terminalServer, "fDenyTSConnections", "0"),
                     RegAdd(terminalServer, "AllowListenersInsideArgonContainer", "1"),
                     RegAdd(rdpTcp, "AllowListenersInsideArgonContainer", "1"),
                     RegAdd(rdpTcp, "SecurityLayer", "0"),
                     RegAdd(rdpTcp, "UserAuthentication", "0"),
                     RegAdd(rdpTcp, "fEnableWinStation", "1"),
                 })
        {
            await RunInContainerAsync(id, command, cancellationToken);
        }

        await RunInContainerAsync(id, "sc.exe config TermService start= auto", cancellationToken);
        await RunInContainerAsync(id, "sc.exe start TermService", cancellationToken);

        // A container's own netstat cannot see its listeners (verified), so the
        // only trustworthy check is a TCP connect from the host.
        var ip = (await GetIpAddressesAsync(id, cancellationToken)).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(ip))
        {
            _logger.LogWarning(
                "Container {SandboxId} has no reachable IP address, so the RDP listener could not be probed.", id);
            return;
        }

        if (await IsTcpPortOpenAsync(ip, ContainerDesktopOptions.RdpPort, cancellationToken))
        {
            _logger.LogInformation(
                "Unexpected: the RDP listener in container {SandboxId} came up on {Ip}:{Port}. RDP into a " +
                "process-isolated container may have become possible on this build; please re-check the " +
                "investigation document.",
                id, ip, ContainerDesktopOptions.RdpPort);
        }
        else
        {
            _logger.LogWarning(
                "As expected, the RDP listener in container {SandboxId} did not come up ({Ip}:{Port} refused); " +
                "termsrv refuses remote connections in a container. Use the console session plus an in-session " +
                "viewer, or a Hyper-V-isolated container.",
                id, ip, ContainerDesktopOptions.RdpPort);
        }
    }

    /// <summary>Builds a <c>reg.exe add … /f</c> command line (values are quoted for the CRT).</summary>
    internal static string RegAdd(string key, string name, string value)
        => $"reg.exe add \"{key}\" /v {name} /t REG_SZ /d \"{value}\" /f";

    /// <summary>Runs a command in the container and returns its exit code (-1 when it could not run).</summary>
    private async Task<int> RunInContainerAsync(
        SandboxInstanceId id, string command, CancellationToken cancellationToken)
    {
        try
        {
            var exitCode = await _process.ExecuteAsync(id.ToString(), command, ct: cancellationToken);
            _logger.LogDebug(
                "Container {SandboxId}: '{Command}' exited with {ExitCode}.", id, command, exitCode);
            return exitCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Container {SandboxId}: '{Command}' could not be run (continuing).", id, command);
            return -1;
        }
    }

    /// <summary>
    /// Opens a TCP connection to <paramref name="host"/>:<paramref name="port"/>
    /// with a short timeout — the only reliable "is anything listening in the
    /// container" check (a container's netstat cannot see its own listeners).
    /// </summary>
    private static async Task<bool> IsTcpPortOpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void RememberContainer(
        string key,
        IDisposable handle,
        SandboxRuntimeInfo runtime,
        string scratchPath,
        IReadOnlyList<string> baseLayers,
        string? namespaceId,
        string? endpointId,
        bool adopted)
    {
        var live = new LiveContainer(handle, runtime, scratchPath, baseLayers, namespaceId, endpointId, adopted);

        if (_containers.TryRemove(key, out var stale))
        {
            try
            {
                stale.Handle.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Replacing the handle for container {SandboxId} failed.", key);
            }
        }

        _containers[key] = live;
    }

    /// <summary>The deterministic HNS endpoint name for a container (HNS object names must be unique).</summary>
    internal static string EndpointName(SandboxInstanceId id) => $"wsm-container-{id.Value:N}";

    /// <summary>
    /// Derives the layer GUID from a registered layer path
    /// (<c>...\Layers\&lt;guid&gt;\Files</c>). Returns <c>null</c> for the
    /// <c>BaseImages</c> fallback layout, where the layer id is not embedded.
    /// </summary>
    internal static string? TryGetLayerId(string baseLayerPath)
    {
        var match = LayerIdPattern.Match(baseLayerPath);
        return match.Success ? match.Groups["id"].Value : null;
    }

    /// <summary>
    /// Turns the sandbox name into a container host name (a short, DNS-safe
    /// label of ASCII letters/digits/hyphens). Falls back to a fixed prefix when
    /// the name has no usable characters.
    /// </summary>
    internal static string ToHostName(string name, string id)
    {
        var sanitized = new string(name
            .Where(ch => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '-')
            .ToArray())
            .Trim('-');

        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = "sandbox";
        }

        var suffix = id.Replace("-", string.Empty);
        var host = $"{sanitized}-{suffix}";
        return host.Length <= 15 ? host : host[..15];
    }

    /// <summary>
    /// Resolves where a mapped host folder lands in the container. An empty
    /// destination (the .wsb "container desktop" convention) maps to a folder
    /// named after the host folder under the container root.
    /// </summary>
    private static string ResolveContainerPath(MappedFolder folder)
    {
        if (!string.IsNullOrWhiteSpace(folder.SandboxFolder))
        {
            return folder.SandboxFolder;
        }

        var name = Path.GetFileName(folder.HostFolder.TrimEnd('\\', '/'));
        return string.IsNullOrWhiteSpace(name) ? @"C:\mapped" : $@"C:\{name}";
    }
}
