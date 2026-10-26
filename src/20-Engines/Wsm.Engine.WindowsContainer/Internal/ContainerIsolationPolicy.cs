using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Volo.Abp.DependencyInjection;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Storage;

namespace Wsm.Engine.WindowsContainer.Internal;

/// <summary>Host OS build and SKU, read from the registry (with a safe fallback).</summary>
internal sealed record HostOsInfo(int Build, bool IsWindows11Client)
{
    /// <summary>The oldest build of Windows Server that Windows 11 may run process isolated.</summary>
    public const int MinimumServerBuild = 20348; // Windows Server 2022

    public static HostOsInfo Read()
    {
        var build = Environment.OSVersion.Version.Build;
        var isServer = false;

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("CurrentBuildNumber") is string text && int.TryParse(text, out var parsed))
            {
                build = parsed;
            }

            isServer = key?.GetValue("InstallationType") is string type
                       && type.Equals("Server", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Registry unavailable: fall back to the reported version.
        }

        // Windows 11 is build 22000+; process isolation is only offered on a client.
        return new HostOsInfo(build, IsWindows11Client: build >= 22000 && !isServer);
    }
}

/// <summary>Seam for tests to supply a host version.</summary>
internal interface IHostOsInfoProvider
{
    HostOsInfo Read();
}

internal sealed class RegistryHostOsInfoProvider : IHostOsInfoProvider, ISingletonDependency
{
    public HostOsInfo Read() => HostOsInfo.Read();
}

/// <summary>Seam for tests to supply imported images without touching the filesystem.</summary>
internal interface IContainerImageCatalog
{
    IReadOnlyList<ContainerImageCandidate> FindCandidates();
}

/// <summary>Scans the default and legacy image roots on disk.</summary>
internal sealed class FileSystemContainerImageCatalog : IContainerImageCatalog, ISingletonDependency
{
    public IReadOnlyList<ContainerImageCandidate> FindCandidates() => ContainerImagePaths.FindImportedImages();
}

/// <summary>
/// Default <see cref="IContainerIsolationPolicy"/>. Resolves the image (engine
/// option, then the <c>WSM_CONTAINER_BASE_IMAGE</c> override, then a compatible
/// imported image, then the newest Windows Sandbox image layer), reads its
/// build/SKU (<see cref="ContainerImageMetadata"/>) and applies Microsoft's
/// compatibility rule.
/// </summary>
[ExposeServices(typeof(IContainerIsolationPolicy))]
internal sealed class ContainerIsolationPolicy : IContainerIsolationPolicy, ISingletonDependency
{
    /// <summary>An override the GUI and CLI set when an image is imported.</summary>
    internal const string ImageOverrideVariable = WindowsContainerEngineOptions.ImageOverrideVariable;

    private readonly IOptions<WindowsContainerEngineOptions> _options;
    private readonly IHcsHostLayout _layout;
    private readonly IHostOsInfoProvider _host;
    private readonly IContainerImageCatalog _images;
    private readonly ILogger<ContainerIsolationPolicy> _logger;
    private readonly object _gate = new();

    private ContainerIsolationSupport? _cached;

    public ContainerIsolationPolicy(
        IOptions<WindowsContainerEngineOptions> options,
        IHcsHostLayout layout,
        IHostOsInfoProvider host,
        IContainerImageCatalog images,
        ILogger<ContainerIsolationPolicy> logger)
    {
        _options = options;
        _layout = layout;
        _host = host;
        _images = images;
        _logger = logger;
    }

    public ContainerIsolationSupport Evaluate()
    {
        lock (_gate)
        {
            return _cached ??= Compute();
        }
    }

    public ContainerIsolationSupport Evaluate(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return Evaluate();
        }

        lock (_gate)
        {
            return Compute(imagePath);
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private ContainerIsolationSupport Compute(string? explicitImagePath = null)
    {
        var host = _host.Read();
        var imagePath = explicitImagePath ?? ResolveImagePath(host);

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return Unsupported(host, "Container.Reason.NoImage");
        }

        if (!ContainerImagePaths.IsPreparedLayer(imagePath))
        {
            return Unsupported(host, "Container.Reason.UnknownImage", imagePath);
        }

        var metadata = ContainerImageMetadata.TryRead(imagePath);
        if (metadata is not { Build: > 0 })
        {
            return Unsupported(host, "Container.Reason.UnknownImage", imagePath);
        }

        metadata = ContainerImagePaths.EnrichMetadataFromFolderName(imagePath, metadata);
        if (IsCompatible(host, metadata))
        {
            var reason = metadata.Build == host.Build
                ? "Container.Reason.SupportedExact"
                : "Container.Reason.SupportedServer";
            return Supported(host, reason, imagePath, metadata);
        }

        return Unsupported(host, "Container.Reason.BuildMismatch", imagePath, metadata);
    }

    /// <summary>
    /// Microsoft's compatibility rule: an exact build match is always allowed,
    /// and a Windows 11 host additionally accepts a Windows Server 2022+ image
    /// whose build does not exceed the host's.
    /// </summary>
    private static bool IsCompatible(HostOsInfo host, ContainerImageMetadata metadata)
        => metadata.Build == host.Build
           || (host.IsWindows11Client
               && metadata.IsServer
               && metadata.Build >= HostOsInfo.MinimumServerBuild
               && metadata.Build <= host.Build);

    /// <summary>
    /// The image layer the engine would use: the environment override, then the
    /// configured option, then the first compatible image the user already
    /// imported, then the newest Windows Sandbox image layer.
    /// </summary>
    private string? ResolveImagePath(HostOsInfo host)
    {
        var configured = Environment.GetEnvironmentVariable(ImageOverrideVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = _options.Value.BaseImageLayerPath;
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        // An image the user imported by hand wins over the Windows Sandbox
        // client layer, which only helps on an exact build match.
        foreach (var candidate in _images.FindCandidates())
        {
            if (IsCompatible(host, candidate.Metadata))
            {
                return candidate.Path;
            }
        }

        var layer = _layout.ResolveBaseImageLayer();
        if (layer?.Path is { Length: > 0 } sandboxLayer)
        {
            return sandboxLayer;
        }

        var files = _layout.ResolveBaseLayerPath();
        return files is null ? null : Path.GetDirectoryName(files.TrimEnd('\\', '/'));
    }

    private ContainerIsolationSupport Supported(
        HostOsInfo host, string reason, string imagePath, ContainerImageMetadata metadata)
    {
        _logger.LogInformation(
            "Process isolation is supported ({Reason}): host build {HostBuild}, image build {ImageBuild}, server={Server}.",
            reason, host.Build, metadata.Build, metadata.IsServer);

        return new ContainerIsolationSupport
        {
            IsSupported = true,
            ReasonKey = reason,
            HostBuild = host.Build,
            HostIsWindows11Client = host.IsWindows11Client,
            ImageBuild = metadata.Build,
            ImageIsServer = metadata.IsServer,
            ImagePath = imagePath,
        };
    }

    private ContainerIsolationSupport Unsupported(
        HostOsInfo host, string reason, string? imagePath = null, ContainerImageMetadata? metadata = null)
    {
        _logger.LogInformation(
            "Process isolation is not supported ({Reason}): host build {HostBuild}, image {Image}.",
            reason, host.Build, imagePath ?? "<none>");

        return new ContainerIsolationSupport
        {
            IsSupported = false,
            ReasonKey = reason,
            HostBuild = host.Build,
            HostIsWindows11Client = host.IsWindows11Client,
            ImageBuild = metadata?.Build,
            ImageIsServer = metadata?.IsServer ?? false,
            ImagePath = imagePath,
        };
    }
}
