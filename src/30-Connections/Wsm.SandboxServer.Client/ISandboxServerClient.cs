using Wsm.Domain.ValueObjects;

namespace Wsm.SandboxServer.Client;

/// <summary>
/// The primary control-plane client: drives <c>WindowsSandboxServer.exe</c>
/// through its gRPC service (<c>sandboxserver.SandboxCore</c>) over the control
/// named pipe. This is the same path Microsoft's <c>wsb.exe</c> and the Windows
/// Sandbox app use, and it works without loading any Microsoft assembly.
/// </summary>
public interface ISandboxServerClient
{
    Task<IReadOnlyList<SandboxInstanceId>> EnumerateRunningAsync(CancellationToken cancellationToken = default);

    Task<SandboxRuntimeInfo> StartAsync(SandboxInstanceId id, string recipeXml, CancellationToken cancellationToken = default);

    Task ShutdownAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    Task MapFolderAsync(SandboxInstanceId id, string hostPath, string sandboxPath, bool allowWrite, CancellationToken cancellationToken = default);

    Task<int> ExecuteAsync(SandboxInstanceId id, string commandLine, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    Task<SandboxRuntimeInfo?> GetRuntimeInfoAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    Task<SandboxTerminationReason> WaitForTerminationAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);
}
