using Volo.Abp.DependencyInjection;
using Wsm.Connection.RdpHost;
using Wsm.Domain.ValueObjects;

namespace Wsm.Application.Internal;

/// <summary>
/// Default <see cref="IRdpSandboxControl"/> over <see cref="ISandboxManager"/>.
/// It lets the RDP connector's toolbar drive the sandbox lifecycle in-process:
/// power on / restart start the instance, shutdown / force shut down stop it.
/// </summary>
[ExposeServices(typeof(IRdpSandboxControl))]
internal sealed class SandboxManagerRdpControl : IRdpSandboxControl, ITransientDependency
{
    private readonly ISandboxManager _manager;

    public SandboxManagerRdpControl(ISandboxManager manager)
    {
        _manager = manager;
    }

    public async Task PowerOnAsync(Guid id, SandboxConnectionMode mode, CancellationToken cancellationToken = default)
    {
        await _manager.StartAsync(id, mode, cancellationToken).ConfigureAwait(false);
    }

    public Task ShutdownAsync(Guid id, CancellationToken cancellationToken = default)
        => _manager.StopAsync(id, cancellationToken);

    public Task ForceShutdownAsync(Guid id, CancellationToken cancellationToken = default)
        => _manager.StopAsync(id, cancellationToken);

    public async Task<SandboxRuntimeInfo> RestartAsync(
        Guid id,
        SandboxConnectionMode mode,
        CancellationToken cancellationToken = default)
    {
        await _manager.StopAsync(id, cancellationToken).ConfigureAwait(false);
        var dto = await _manager.StartAsync(id, mode, cancellationToken).ConfigureAwait(false);

        return dto.Runtime
            ?? await _manager.GetRuntimeAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Sandbox '{id}' did not report a runtime after a restart.");
    }

    public async Task<bool> IsRunningAsync(Guid id, CancellationToken cancellationToken = default)
        => await _manager.GetRuntimeAsync(id, cancellationToken).ConfigureAwait(false) is not null;
}
