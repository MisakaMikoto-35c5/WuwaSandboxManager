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

/// <summary>
/// Default <see cref="IContainerIsolationPolicy"/>. Resolves the image (engine
/// option, then the <c>WSM_CONTAINER_BASE_IMAGE</c> override, then the newest
/// Windows Sandbox image layer), reads its build/SKU (<see cref="ContainerImageMetadata"/>)
/// and applies Microsoft's compatibility rule.
/// </summary>
[ExposeServices(typeof(IContainerIsolationPolicy))]
internal sealed class ContainerIsolationPolicy : IContainerIsolationPolicy, ISingletonDependency
{
    /// <summary>An override the GUI and CLI set when an image is imported.</summary>
    internal const string ImageOverrideVariable = WindowsContainerEngineOptions.ImageOverrideVariable;

    private readonly IOptions<WindowsContainerEngineOptions> _options;
    private readonly IHcsHostLayout _layout;
    private readonly IHostOsInfoProvider _host;
    private readonly ILogger<ContainerIsolationPolicy> _logger;
    private readonly object _gate = new();

    private ContainerIsolationSupport? _cached;

    public ContainerIsolationPolicy(
        IOptions<WindowsContainerEngineOptions> options,
        IHcsHostLayout layout,
        IHostOsInfoProvider host,
        ILogger<ContainerIsolationPolicy> logger)
    {
        _options = options;
        _layout = layout;
        _host = host;
        _logger = logger;
    }

    public ContainerIsolationSupport Evaluate()
    {
        lock (_gate)
        {
            return _cached ??= Compute();
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private ContainerIsolationSupport Compute()
    {
        var host = _host.Read();
        var imagePath = ResolveImagePath();

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return Unsupported(host, "Container.Reason.NoImage");
        }

        var metadata = ContainerImageMetadata.TryRead(imagePath);
        if (metadata is null)
        {
            return Unsupported(host, "Container.Reason.UnknownImage", imagePath);
        }

        var imageBuild = metadata.Build;

        // 1. An exact build match is always allowed.
        if (imageBuild == host.Build)
        {
            return Supported(host, "Container.Reason.SupportedExact", imagePath, metadata);
        }

        // 2. Windows 11 may run a Windows Server 2022/2025 image despite the mismatch.
        if (host.IsWindows11Client
            && metadata.IsServer
            && imageBuild >= HostOsInfo.MinimumServerBuild
            && imageBuild <= host.Build)
        {
            return Supported(host, "Container.Reason.SupportedServer", imagePath, metadata);
        }

        return Unsupported(host, "Container.Reason.BuildMismatch", imagePath, metadata);
    }

    /// <summary>
    /// The image layer the engine would use: the configured option, then the
    /// environment override, then the newest Windows Sandbox image layer.
    /// </summary>
    private string? ResolveImagePath()
    {
        var configured = _options.Value.BaseImageLayerPath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = Environment.GetEnvironmentVariable(ImageOverrideVariable);
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
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
