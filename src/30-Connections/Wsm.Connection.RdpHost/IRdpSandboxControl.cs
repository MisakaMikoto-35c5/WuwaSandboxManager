using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.RdpHost;

/// <summary>
/// The sandbox lifecycle operations the RDP connector's toolbar exposes. The
/// connector does not own sandbox state; it asks this façade to start, stop or
/// restart the sandbox on its behalf. Implemented by the application layer over
/// <c>ISandboxManager</c> (and, in service mode, the agent).
/// <para>
/// Every operation is VM-level: <see cref="ShutdownAsync"/> and
/// <see cref="ForceShutdownAsync"/> both stop the compute system (the engine
/// escalates from a graceful ACPI shutdown to a terminate), and
/// <see cref="RestartAsync"/> stops then starts it (the guest state is
/// discarded, as with any Windows Sandbox restart).
/// </para>
/// </summary>
public interface IRdpSandboxControl
{
    /// <summary>Starts (powers on) the sandbox.</summary>
    Task PowerOnAsync(Guid id, SandboxConnectionMode mode, CancellationToken cancellationToken = default);

    /// <summary>Stops the sandbox (graceful shutdown, escalating to terminate).</summary>
    Task ShutdownAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the sandbox immediately. The engine's stop sequence already escalates
    /// to an explicit terminate; this exists so the UI can express intent.
    /// </summary>
    Task ForceShutdownAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops then starts the sandbox and returns the new live runtime, which the
    /// connector uses to re-establish the session.
    /// </summary>
    Task<SandboxRuntimeInfo> RestartAsync(Guid id, SandboxConnectionMode mode, CancellationToken cancellationToken = default);

    /// <summary>Whether the sandbox currently reports a live runtime.</summary>
    Task<bool> IsRunningAsync(Guid id, CancellationToken cancellationToken = default);
}
