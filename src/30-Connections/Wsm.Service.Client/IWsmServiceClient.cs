using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;

namespace Wsm.Service.Client;

/// <summary>
/// Client for the resident WSM agent service, reached over the fixed control
/// named pipe (<c>\\.\pipe\wsm-agent</c>) via gRPC. Exposes sandbox VM lifecycle
/// plus in-guest operations. Failures surface as <c>Wsm.Domain</c> exceptions so
/// callers handle the same types whether the work ran in-process or over the pipe.
/// </summary>
public interface IWsmServiceClient
{
    /// <summary>Lists the ids of the sandboxes known to the service.</summary>
    Task<IReadOnlyList<Guid>> EnumerateAsync(CancellationToken ct = default);

    /// <summary>Starts the sandbox <paramref name="id"/> and returns its live runtime.</summary>
    Task<SandboxRuntimeInfo> StartAsync(Guid id, SandboxConnectionMode mode, CancellationToken ct = default);

    /// <summary>Stops the sandbox <paramref name="id"/>. Already-stopped instances are ignored.</summary>
    Task StopAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Returns the live runtime for <paramref name="id"/>. Returns <c>null</c> when
    /// the instance exists but is not currently running; a missing instance surfaces
    /// as <see cref="Wsm.Domain.Exceptions.SandboxNotFoundException"/>.
    /// </summary>
    Task<SandboxRuntimeInfo?> GetRuntimeAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Server-side pre-connect preparation: the service reclaims any orphaned
    /// guest sessions and returns the engine-backed live runtime, which the caller
    /// then uses to establish the RDP session itself. A not-running instance
    /// surfaces as <see cref="Wsm.Domain.Exceptions.SandboxDomainException"/>; a
    /// missing instance surfaces as
    /// <see cref="Wsm.Domain.Exceptions.SandboxNotFoundException"/>.
    /// </summary>
    Task<SandboxRuntimeInfo> PrepareConnectAsync(Guid id, CancellationToken ct = default);

    /// <summary>Runs <paramref name="commandLine"/> in the guest and returns its result.</summary>
    Task<GuestCommandResult> ExecuteAsync(Guid id, string commandLine, CancellationToken ct = default);

    /// <summary>Shares <paramref name="folder"/> into the sandbox <paramref name="id"/>.</summary>
    Task MapFolderAsync(Guid id, MappedFolder folder, CancellationToken ct = default);

    /// <summary>Returns the guest IP addresses for <paramref name="id"/>.</summary>
    Task<IReadOnlyList<string>> GetIpAddressesAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Cheap availability probe: attempts a trivial RPC with a short timeout.
    /// Any failure (including no service listening) returns <c>false</c>; never throws.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Asks the agent to shut down (for stopping a standalone console agent; an
    /// SCM-installed service is stopped through the Service Control Manager).
    /// A transport failure right after the reply — the agent exiting — is
    /// treated as success.
    /// </summary>
    Task ShutdownAsync(CancellationToken ct = default);

    /// <summary>
    /// Probes the agent and, when it is absent, starts a standalone
    /// (<c>--console</c>) agent process and waits for its pipe to come up.
    /// Returns true when an agent answers afterwards. Never throws.
    /// </summary>
    Task<bool> EnsureAvailableAsync(string? dataFolder = null, CancellationToken ct = default);
}
