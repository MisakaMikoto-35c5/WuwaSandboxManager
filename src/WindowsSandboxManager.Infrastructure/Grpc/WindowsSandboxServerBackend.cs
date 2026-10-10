using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using WindowsSandboxManager.Domain.Abstractions;
using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;
using WindowsSandboxManager.Infrastructure.Grpc.Generated;

namespace WindowsSandboxManager.Infrastructure.Grpc;

/// <summary>
/// The primary control plane: drives <c>WindowsSandboxServer.exe</c> through its
/// gRPC service (<c>sandboxserver.SandboxCore</c>) over the control named pipe.
/// This is the same path Microsoft's <c>wsb.exe</c> and the Windows Sandbox app
/// use, and it works without loading any Microsoft assembly.
/// </summary>
public sealed class WindowsSandboxServerBackend : ISandboxBackend
{
    public const int CoEAppSingleUse = unchecked((int)0x800401F6);
    public const int ENotFound = unchecked((int)0x80070490);
    public const int ServerUnavailable = unchecked((int)0x800706BA);

    private readonly ISandboxEnvironment _environment;
    private readonly ISandboxRecipeSerializer _serializer;
    private readonly WindowsSandboxGrpcChannelFactory _channelFactory;
    private readonly ILogger<WindowsSandboxServerBackend> _logger;

    public WindowsSandboxServerBackend(
        ISandboxEnvironment environment,
        ISandboxRecipeSerializer serializer,
        WindowsSandboxGrpcChannelFactory channelFactory,
        ILogger<WindowsSandboxServerBackend> logger)
    {
        _environment = environment;
        _serializer = serializer;
        _channelFactory = channelFactory;
        _logger = logger;
    }

    public string Name => "WindowsSandboxServer (gRPC)";

    public SandboxBackendCapabilities Capabilities => new()
    {
        SupportsMultipleInstances = _environment.IsMultipleInstancesAllowed,
        SupportsRuntimeFolderMapping = true,
        SupportsProcessExecution = true,
        SupportsEnumeration = true,
        SupportsReboot = true,
        SupportsCpuConfiguration = false,
        SupportsLiveMemoryResize = false,
    };

    public async Task<IReadOnlyList<SandboxInstanceId>> EnumerateRunningAsync(CancellationToken cancellationToken = default)
    {
        var reply = await WithClientAsync(c => c.EnumerateSandboxVMsAsync(new Empty(), cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        ThrowOnHresult(reply.Hresult, "EnumerateSandboxVMs");

        return reply.SandboxIds
            .Select(id => SandboxInstanceId.TryParse(id, out var parsed) ? parsed : (SandboxInstanceId?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();
    }

    public async Task<SandboxRuntimeInfo> StartAsync(
        SandboxRecipe recipe,
        SandboxInstanceId id,
        CancellationToken cancellationToken = default)
    {
        var config = _serializer.Serialize(recipe);
        var request = new StartRequest
        {
            Config = config,
            Id = id.ToString(),
        };

        var reply = await WithClientAsync(c => c.StartSandboxAsync(request, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        _logger.LogInformation(
            "StartSandbox request: hasId={HasId} id={Id} hasConfig={HasConfig} configLength={ReqLen}; reply: hresult=0x{HResult:X8} configLength={Length}",
            request.HasId, request.Id, request.HasConfig, config?.Length ?? -1,
            reply.Hresult, reply.RdpClientConfig?.Length ?? -1);
        ThrowOnHresult(reply.Hresult, "StartSandbox");

        var runtime = RdpClientConfigParser.Parse(reply.RdpClientConfig, id);
        if (string.IsNullOrWhiteSpace(reply.RdpClientConfig) || runtime.VmId == Guid.Empty)
        {
            throw new SandboxBackendException(
                "WindowsSandboxServer returned an empty runtime configuration; the instance was not created.",
                0);
        }

        _logger.LogInformation("Started sandbox {SandboxId} through the control plane", id);
        return runtime;
    }

    public async Task ShutdownAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await WithClientAsync(c => c.ShutdownSandboxAsync(
                new ShutdownRequest { SandboxId = id.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            ThrowOnHresult(reply.Hresult, "ShutdownSandbox");
        }
        catch (SandboxNotFoundException)
        {
            // Already gone; treat as stopped.
        }
    }

    public async Task MapFolderAsync(SandboxInstanceId id, MappedFolder folder, CancellationToken cancellationToken = default)
    {
        var request = new MapFolderRequest
        {
            SandboxId = id.ToString(),
            HostPath = folder.HostFolder,
            SandboxPath = folder.SandboxFolder,
            AllowWrite = folder.AllowWrite,
        };

        var reply = await WithClientAsync(c => c.MapFolderAsync(request, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        ThrowOnHresult(reply.Hresult, "MapFolder");
    }

    public async Task<int> ExecuteAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default)
    {
        var request = new CreateProcessRequest
        {
            SandboxId = id.ToString(),
            CommandLine = command.Command,
            WorkingDirectory = string.Empty,
            RunAs = Generated.RunAs.System,
        };

        var reply = await WithClientAsync(c => c.CreateProcessAsync(request, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        ThrowOnHresult(reply.Hresult, "CreateProcess");
        return reply.ProcessExitCode;
    }

    public async Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var reply = await WithClientAsync(c => c.GetIpAddressAsync(
            new IpAddressRequest { SandboxId = id.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        ThrowOnHresult(reply.Hresult, "GetIpAddress");
        return reply.IpAddresses.ToArray();
    }

    public async Task<SandboxRuntimeInfo?> GetRuntimeInfoAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await WithClientAsync(c => c.GetRdpClientConfigAsync(
                new RdpClientConfigRequest { SandboxId = id.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            ThrowOnHresult(reply.Hresult, "GetRdpClientConfig");
            return RdpClientConfigParser.Parse(reply.RdpClientConfig, id);
        }
        catch (SandboxNotFoundException)
        {
            return null;
        }
    }

    public async Task<SandboxTerminationReason> WaitForTerminationAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var reply = await WithClientAsync(c => c.GetTerminateReasonAsync(
            new VMTerminateReasonRequest { SandboxId = id.ToString() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        ThrowOnHresult(reply.Hresult, "GetTerminateReason");
        return (SandboxTerminationReason)reply.TerminatedReason;
    }

    private async Task<T> WithClientAsync<T>(Func<SandboxCore.SandboxCoreClient, AsyncUnaryCall<T>> action)
    {
        if (!_environment.EnsureServerRunning())
        {
            throw new SandboxNotSupportedException(
                "The Windows Sandbox control server (WindowsSandboxServer.exe) could not be started. " +
                "Ensure the Windows Sandbox optional feature is enabled.");
        }

        using var channel = _channelFactory.CreateChannel();
        var client = new SandboxCore.SandboxCoreClient(channel);

        try
        {
            return await action(client).ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            var code = ex.StatusCode == StatusCode.Unavailable ? ServerUnavailable : 0;
            throw new SandboxBackendException(
                $"Windows Sandbox control-plane call failed: {ex.StatusCode} - {ex.Status.Detail}", code, ex);
        }
    }

    private static void ThrowOnHresult(int hresult, string operation)
    {
        if (hresult == 0)
        {
            return;
        }

        if (hresult == CoEAppSingleUse)
        {
            throw new MultipleInstancesNotAllowedException(
                "The host policy only allows a single Windows Sandbox instance at a time " +
                "(CO_E_APPSINGLEUSE). Stop the running instance, or use a multi-instance capable backend.");
        }

        if (hresult == ENotFound)
        {
            throw new SandboxNotFoundException($"The sandbox was not found while calling {operation}.");
        }

        throw new SandboxBackendException(
            $"Windows Sandbox operation '{operation}' failed with HRESULT 0x{hresult:X8}.", hresult);
    }
}
