using Volo.Abp.DependencyInjection;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Wsm.Host.Environment;
using Wsm.Recipe.Serialization;
using Wsm.SandboxServer.Client;

namespace Wsm.Engine.WindowsSandbox;

/// <summary>
/// The primary, non-elevated control plane: drives <c>WindowsSandboxServer.exe</c>
/// through its gRPC service (<c>sandboxserver.SandboxCore</c>) over the control
/// named pipe. This is the same path Microsoft's <c>wsb.exe</c> and the Windows
/// Sandbox app use, and it works without loading any Microsoft assembly.
/// </summary>
[ExposeServices(typeof(ISandboxEngine))]
internal sealed class WindowsSandboxEngine : ISandboxEngine, ISingletonDependency
{
    private readonly ISandboxServerClient _client;
    private readonly ISandboxRecipeSerializer _serializer;
    private readonly ISandboxEnvironment _environment;

    public WindowsSandboxEngine(
        ISandboxServerClient client,
        ISandboxRecipeSerializer serializer,
        ISandboxEnvironment environment)
    {
        _client = client;
        _serializer = serializer;
        _environment = environment;
    }

    public string Name => "WindowsSandboxServer (gRPC)";

    public SandboxEngineKind Kind => SandboxEngineKind.WindowsSandbox;

    public bool IsAvailable => true;

    public SandboxEngineCapabilities Capabilities => new()
    {
        SupportsMultipleInstances = _environment.IsMultipleInstancesAllowed,
        SupportsRuntimeFolderMapping = true,
        SupportsProcessExecution = true,
        SupportsEnumeration = true,
        SupportsCpuConfiguration = false,
        SupportsCustomStorageLocation = false,
        SupportsCustomBaseImage = false,
        SupportsCheckpoints = false,
        RequiresElevation = false,
    };

    public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
        => _client.EnumerateRunningAsync(cancellationToken);

    public Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default)
    {
        var xml = _serializer.Serialize(request.Recipe);

        // The single-instance policy failure (CO_E_APPSINGLEUSE) is mapped to
        // MultipleInstancesNotAllowedException by the control-plane client.
        return _client.StartAsync(request.Id, xml, cancellationToken);
    }

    public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
        => _client.ShutdownAsync(id, cancellationToken);

    public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
        => _client.GetRuntimeInfoAsync(id, cancellationToken);

    public Task MapFolderAsync(SandboxInstanceId id, MappedFolder folder, CancellationToken cancellationToken = default)
        => _client.MapFolderAsync(id, folder.HostFolder, folder.SandboxFolder, folder.AllowWrite, cancellationToken);

    public Task<int> ExecuteAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default)
        => _client.ExecuteAsync(id, command.Command, cancellationToken);

    public Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
        => _client.GetIpAddressesAsync(id, cancellationToken);
}
