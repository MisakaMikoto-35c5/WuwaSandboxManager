using Microsoft.Extensions.Logging;
using Wsm.Application;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Wsm.Service.Contracts.Internal;

namespace Wsm.Service.Client;

/// <summary>
/// <see cref="ISandboxManager"/> decorator that routes VM-lifecycle calls through
/// the resident WSM agent (the maintenance process) so the VMs are owned — and
/// kept alive — by the agent instead of the caller's process. A front-end that
/// exits (the GUI, a CLI invocation) then leaves running sandboxes untouched,
/// and the next front-end reattaches through the agent.
/// <para>
/// Definition-level operations (create/import/list/get/delete, checkpoints,
/// capabilities, connect) always run in-process: the definitions live in the
/// shared on-disk manifest, and an RDP session is presented by the caller's own
/// process (the GUI spawns a per-session <c>wsm connect</c> child, which routes
/// through the agent itself).
/// </para>
/// <para>
/// The agent is probed (and, when absent, spawned as a standalone console agent)
/// lazily on the first routed call. An agent that turns out to be unreachable
/// (<see cref="WsmServiceStatus.ServerUnavailable"/>) fails the call over to the
/// in-process manager and is not routed to again for the rest of this process.
/// </para>
/// </summary>
public sealed class ServiceBackedSandboxManager : ISandboxManager
{
    private readonly ISandboxManager _inner;
    private readonly IWsmServiceClient _service;
    private readonly string _dataFolder;
    private readonly ILogger<ServiceBackedSandboxManager> _logger;

    private Task<bool>? _agentAvailability;
    private volatile bool _agentDown;

    public ServiceBackedSandboxManager(
        ISandboxManager inner,
        IWsmServiceClient service,
        string dataFolder,
        ILogger<ServiceBackedSandboxManager> logger)
    {
        _inner = inner;
        _service = service;
        _dataFolder = dataFolder;
        _logger = logger;
    }

    public Task<SandboxInstanceDto> CreateAsync(string? name, SandboxRecipe recipe, SandboxEngineKind? engine = null, CancellationToken cancellationToken = default)
        => _inner.CreateAsync(name, recipe, engine, cancellationToken);

    public Task<IReadOnlyList<SandboxInstanceDto>> ListAsync(CancellationToken cancellationToken = default)
        => _inner.ListAsync(cancellationToken);

    public Task<SandboxInstanceDto> ImportAsync(string manifestPath, CancellationToken cancellationToken = default)
        => _inner.ImportAsync(manifestPath, cancellationToken);

    public Task<SandboxInstanceDto> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => _inner.GetAsync(id, cancellationToken);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => _inner.DeleteAsync(id, cancellationToken);

    public Task<SandboxInstanceDto> StartAsync(Guid id, SandboxConnectionMode mode, CancellationToken cancellationToken = default)
        => RouteAsync(
            async () =>
            {
                var runtime = await _service.StartAsync(id, mode, cancellationToken).ConfigureAwait(false);

                // The DTO shape (name, recipe, state, …) comes from the shared
                // on-disk manifest via the in-process manager; the runtime lives
                // in the agent process, so it is filled in from the Start reply
                // when the in-process read-back cannot see it.
                var dto = await _inner.GetAsync(id, cancellationToken).ConfigureAwait(false);
                return dto.Runtime is null ? dto with { Runtime = runtime } : dto;
            },
            () => _inner.StartAsync(id, mode, cancellationToken),
            cancellationToken);

    public Task StopAsync(Guid id, CancellationToken cancellationToken = default)
        => RouteAsync(
            async () =>
            {
                await _service.StopAsync(id, cancellationToken).ConfigureAwait(false);
                return true;
            },
            async () =>
            {
                await _inner.StopAsync(id, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    public Task<IReadOnlyList<Guid>> ListRunningAsync(SandboxEngineKind? preferredEngine = null, CancellationToken cancellationToken = default)
        => preferredEngine is null
            ? RouteAsync(
                () => _service.EnumerateAsync(cancellationToken),
                () => _inner.ListRunningAsync(null, cancellationToken),
                cancellationToken)
            : _inner.ListRunningAsync(preferredEngine, cancellationToken);

    public Task<SandboxCheckpoint> SaveCheckpointAsync(Guid id, string name, SandboxCheckpointType type, CancellationToken cancellationToken = default)
        => _inner.SaveCheckpointAsync(id, name, type, cancellationToken);

    public Task<IReadOnlyList<SandboxCheckpoint>> ListCheckpointsAsync(Guid id, CancellationToken cancellationToken = default)
        => _inner.ListCheckpointsAsync(id, cancellationToken);

    public Task<Guid> RestoreCheckpointAsync(Guid id, Guid checkpointId, CancellationToken cancellationToken = default)
        => _inner.RestoreCheckpointAsync(id, checkpointId, cancellationToken);

    public Task<SandboxRuntimeInfo?> GetRuntimeAsync(Guid id, CancellationToken cancellationToken = default)
        => RouteAsync(
            () => _service.GetRuntimeAsync(id, cancellationToken),
            () => _inner.GetRuntimeAsync(id, cancellationToken),
            cancellationToken);

    public Task<SandboxRuntimeInfo> PrepareConnectAsync(Guid id, CancellationToken cancellationToken = default)
        => RouteAsync(
            () => _service.PrepareConnectAsync(id, cancellationToken),
            () => _inner.PrepareConnectAsync(id, cancellationToken),
            cancellationToken);

    // Sessions are presented by the caller's own process (the GUI spawns a
    // per-session 'wsm connect' child, which routes through the agent itself).
    public Task ConnectAsync(Guid id, SandboxConnectionMode mode, string? remoteAppProgram = null, string? remoteAppArguments = null, CancellationToken cancellationToken = default)
        => _inner.ConnectAsync(id, mode, remoteAppProgram, remoteAppArguments, cancellationToken);

    public Task<IReadOnlyList<string>> GetIpAddressesAsync(Guid id, CancellationToken cancellationToken = default)
        => RouteAsync(
            () => _service.GetIpAddressesAsync(id, cancellationToken),
            () => _inner.GetIpAddressesAsync(id, cancellationToken),
            cancellationToken);

    public Task MapFolderAsync(Guid id, MappedFolder folder, CancellationToken cancellationToken = default)
        => RouteAsync(
            async () =>
            {
                await _service.MapFolderAsync(id, folder, cancellationToken).ConfigureAwait(false);
                return true;
            },
            async () =>
            {
                await _inner.MapFolderAsync(id, folder, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    public async Task<int> ExecuteAsync(Guid id, string command, CancellationToken cancellationToken = default)
        => await RouteAsync(
            async () => (await _service.ExecuteAsync(id, command, cancellationToken).ConfigureAwait(false)).ExitCode,
            () => _inner.ExecuteAsync(id, command, cancellationToken),
            cancellationToken).ConfigureAwait(false);

    public Task<SandboxCapabilitiesDto> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        => _inner.GetCapabilitiesAsync(cancellationToken);

    /// <summary>Probes (and on first use spawns) the agent; the result is cached for the process.</summary>
    private Task<bool> AgentAvailableAsync(CancellationToken ct)
        => _agentAvailability ??= _service.EnsureAvailableAsync(_dataFolder, ct);

    private async Task<T> RouteAsync<T>(Func<Task<T>> viaAgent, Func<Task<T>> inProcess, CancellationToken ct)
    {
        if (!_agentDown && await AgentAvailableAsync(ct).ConfigureAwait(false))
        {
            try
            {
                return await viaAgent().ConfigureAwait(false);
            }
            catch (SandboxBackendException ex) when (ex.HResult == WsmServiceStatus.ServerUnavailable)
            {
                // The agent died between the probe and the call: fail this call
                // over to the in-process manager and stop routing to the agent
                // for the rest of this process.
                _agentDown = true;
                _logger.LogWarning(ex, "The WSM agent became unavailable; falling back to the in-process manager.");
            }
        }

        return await inProcess().ConfigureAwait(false);
    }
}
