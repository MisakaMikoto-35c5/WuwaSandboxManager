using Microsoft.Extensions.Logging;
using Wsm.Connection.ContainerDesktop;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Service.Client;
using Wsm.Service.Contracts;
using Wsm.Service.Contracts.Internal;

namespace Wsm.Ui.WinUI;

/// <summary>
/// Starts container desktop channels for the GUI, routing the way
/// <see cref="ServiceBackedSandboxManager"/> routes VM lifecycle: through the
/// resident WSM agent when one answers (the agent owns the running container, so
/// only it can set the channel up), in-process through
/// <see cref="IContainerDesktopHost"/> when no agent answers (this process then
/// started — and owns — the container itself).
/// <para>
/// Presenting stays client-side: the connector always asks for
/// <c>present = false</c> and the caller launches a <c>wsm desktop-view</c>
/// viewer on the returned frame path (same split as <c>PrepareConnect</c>).
/// </para>
/// </summary>
internal sealed class ContainerDesktopConnector
{
    private readonly IWsmServiceClient? _service;
    private readonly IContainerDesktopHost? _inProcess;
    private readonly string? _dataFolder;
    private readonly ILogger? _logger;

    private Task<bool>? _agentAvailability;
    private volatile bool _agentDown;

    public ContainerDesktopConnector(
        IWsmServiceClient? service,
        IContainerDesktopHost? inProcess,
        string? dataFolder,
        ILogger? logger = null)
    {
        _service = service;
        _inProcess = inProcess;
        _dataFolder = dataFolder;
        _logger = logger;
    }

    /// <summary>
    /// Starts (or adopts) the sandbox's desktop channel with
    /// <paramref name="providerId"/> and returns its status, including the frame
    /// path a viewer attaches to.
    /// </summary>
    public async Task<ContainerDesktopStatus> StartDesktopAsync(
        Guid id, SandboxRecipe recipe, string providerId, CancellationToken cancellationToken = default)
    {
        if (_service is not null && !_agentDown
            && await AgentAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                return await _service
                    .StartDesktopAsync(id, provider: providerId, present: false, ct: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SandboxBackendException ex) when (ex.HResult == WsmServiceStatus.ServerUnavailable)
            {
                // The agent died between the probe and the call: fail over to the
                // in-process channel and stop routing to the agent for the rest
                // of this process.
                _agentDown = true;
                _logger?.LogWarning(
                    ex, "The WSM agent became unavailable; starting the container desktop in-process.");
            }
        }

        if (_inProcess is null)
        {
            throw new SandboxBackendException(
                "The WSM agent is not available and this process cannot host a container desktop channel.", 0);
        }

        var plan = recipe.Desktop with { Provider = providerId, PresentToInteractiveSession = false };
        var session = await _inProcess
            .StartAsync(SandboxInstanceId.From(id), recipe with { Desktop = plan }, cancellationToken)
            .ConfigureAwait(false);

        return new ContainerDesktopStatus(
            Running: true,
            ProviderId: session.ProviderId,
            Kind: session.ProviderId == ContainerDesktopProviderIds.WindowMirror
                ? ContainerDesktopKind.WindowMirror
                : ContainerDesktopKind.FrameBuffer,
            FramePath: session.FramePath,
            Width: session.Geometry.Width,
            Height: session.Geometry.Height,
            Fps: session.Geometry.Fps,
            WindowCount: session.Windows?.Count,
            ViewerProcessId: session.ViewerProcessId);
    }

    /// <summary>Probes (and on first use spawns) the agent; the result is cached for the process.</summary>
    private Task<bool> AgentAvailableAsync(CancellationToken ct)
        => _agentAvailability ??= _service!.EnsureAvailableAsync(_dataFolder, ct);
}
