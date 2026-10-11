using System.Collections.Concurrent;
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
using Wsm.Host.Elevation;

namespace Wsm.Engine.WindowsContainer;

/// <summary>
/// The Windows container engine: HCS process-isolated containers that reuse the
/// <b>minimal OS image layer provisioned by the Windows Sandbox</b>
/// (<see cref="IHcsHostLayout.ResolveBaseLayerPath"/>).
/// <para>
/// This is the lightweight isolation tier — the Windows analogue of the
/// <c>processcontainer</c> backend in Microsoft's MXC (Microsoft eXecution
/// Containers), as opposed to the full Windows Sandbox VM. Instead of booting the
/// layer inside a virtual machine it hands the same layer to HCS as a read-only
/// container layer and gives each instance its own scratch directory.
/// </para>
/// <para>
/// Because a container built from that stripped-down image starts with almost no
/// services, <see cref="StartAsync"/> also brings up the required services — the
/// host services the container stack depends on (<c>vmcompute</c>, <c>hns</c>)
/// and the in-container services configured in
/// <see cref="WindowsContainerEngineOptions.RequiredServices"/>.
/// </para>
/// Like the HCS engine it requires an elevated process (<c>0x8037011B</c>
/// otherwise).
/// </summary>
[ExposeServices(typeof(ISandboxEngine))]
internal sealed class WindowsContainerEngine : ISandboxEngine, ISingletonDependency
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
    private readonly IHnsNetworkService _network;
    private readonly IElevationService _elevation;
    private readonly IOptions<WindowsContainerEngineOptions> _options;
    private readonly ILogger<WindowsContainerEngine> _logger;

    /// <summary>
    /// Live compute systems created by this engine, keyed by compute-system id.
    /// The native handle must be held for the instance's whole lifetime because
    /// the configuration sets <c>ShouldTerminateOnLastHandleClosed=true</c>;
    /// releasing it terminates the container.
    /// </summary>
    private readonly ConcurrentDictionary<string, LiveContainer> _containers =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record LiveContainer(IDisposable Handle, SandboxRuntimeInfo Runtime);

    public WindowsContainerEngine(
        IHcsClient hcs,
        IHcsHostLayout layout,
        IContainerJsonBuilder json,
        IComputeProcessExecutor process,
        IHnsNetworkService network,
        IElevationService elevation,
        IOptions<WindowsContainerEngineOptions> options,
        ILogger<WindowsContainerEngine> logger)
    {
        _hcs = hcs;
        _layout = layout;
        _json = json;
        _process = process;
        _network = network;
        _elevation = elevation;
        _options = options;
        _logger = logger;
    }

    public string Name => "Windows Container (HCS)";

    public SandboxEngineKind Kind => SandboxEngineKind.WindowsContainer;

    public bool IsAvailable => _elevation.IsElevated;

    public SandboxEngineCapabilities Capabilities => new()
    {
        SupportsMultipleInstances = true,
        SupportsCpuConfiguration = true,
        SupportsCustomStorageLocation = true,
        // The base layer is always the resolved Windows Sandbox image; there is
        // no recipe hook for a caller-supplied image yet.
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

        // Reuse the exact base layer the Windows Sandbox provisions; without it
        // there is no image to run as a container.
        var baseLayer = _layout.ResolveBaseLayerPath()
            ?? throw new SandboxNotSupportedException(
                "No usable base OS image layer was found under " +
                $"{_layout.LayersFolder}. Start Windows Sandbox once so the shared " +
                "minimal image is provisioned.");

        EnsureHostServices();

        var storageDirectory = recipe.Storage.HasCustomLocation
            ? recipe.Storage.StoragePath!
            : _layout.GetStorageDirectory(id.ToString());
        var scratchPath = Path.Combine(storageDirectory, "volume");
        Directory.CreateDirectory(scratchPath);

        var definition = new ContainerDefinition
        {
            Id = id.ToString(),
            BaseLayerPath = baseLayer,
            BaseLayerId = TryGetLayerId(baseLayer),
            ScratchPath = scratchPath,
            HostName = ToHostName(recipe.Name.Value, id.ToString()),
            MemoryMb = (ulong)recipe.Memory.MemoryMb,
            CpuCount = (uint)recipe.Cpu.CoreCount,
            // Container networking needs an HNS endpoint attached to the
            // container; that wiring is a follow-up, so no adapters are supplied
            // and the container starts without a NIC.
            EnableNetworking = false,
            NetworkAdapterIds = Array.Empty<string>(),
            MappedFolders = recipe.MappedFolders
                .Select(folder => new ContainerMappedFolder(
                    folder.HostFolder,
                    ResolveContainerPath(folder),
                    folder.ReadOnly))
                .ToArray(),
        };

        var json = _json.Build(definition);
        _logger.LogInformation(
            "Creating Windows container {SandboxId} (base={BaseLayer}, scratch={Scratch}, " +
            "memory={MemoryMb}MB, cpus={CpuCount})",
            id, baseLayer, scratchPath, definition.MemoryMb, definition.CpuCount);

        var system = _hcs.Create(id.ToString(), json);

        try
        {
            _hcs.Start(id.ToString());
        }
        catch (SandboxBackendException ex)
        {
            system.Dispose();
            _logger.LogError(ex, "HCS rejected or failed to start container {SandboxId}", id);
            throw new SandboxBackendException(
                $"HCS rejected the container configuration or failed to start container {id}: {ex.Message}",
                ex.HResult,
                ex);
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
            UserName = "ContainerAdministrator",
            Redirections = recipe.Redirections,
        };

        RememberContainer(id.ToString(), system, runtime);

        // The shared image is minimal; start the configured services. This is
        // best-effort: a service that cannot start is logged, not fatal.
        await EnsureContainerServicesAsync(id, _options.Value.RequiredServices, cancellationToken);

        return runtime;
    }

    public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        var key = id.ToString();

        // Disposing the held handle closes the last handle to the compute system,
        // which terminates it. Fall back to an explicit terminate for containers
        // this process did not create.
        if (_containers.TryRemove(key, out var live))
        {
            live.Handle.Dispose();
        }
        else
        {
            _hcs.Terminate(key);
        }

        return Task.CompletedTask;
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

    public Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        return _network.GetIpAddressesAsync(id.ToString(), cancellationToken);
    }

    /// <summary>
    /// Reclaims the container's on-disk footprint (its own <c>volume</c> scratch
    /// folder, not the whole per-sandbox folder, which also holds the manifest).
    /// Best effort: a partial failure is logged, never thrown, so the caller's
    /// delete still completes. The container is expected to be stopped first.
    /// </summary>
    public Task DeleteDataAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var key = id.ToString();
        var scratch = Path.Combine(_layout.GetStorageDirectory(key), "volume");

        try
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
                _logger.LogInformation(
                    "Deleted container scratch '{Directory}' for container {SandboxId}.",
                    scratch, key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to delete container scratch '{Directory}' for {SandboxId} (continuing).",
                scratch, key);
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
                        "Could not start required host service '{Service}'; container creation may fail.",
                        service);
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

    private void RememberContainer(string key, IDisposable handle, SandboxRuntimeInfo runtime)
    {
        var live = new LiveContainer(handle, runtime);

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
